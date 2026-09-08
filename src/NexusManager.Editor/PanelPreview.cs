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

    /// <summary>Largest factor this preview will use. The fitted factor is
    /// chosen per layout pass and is never larger than this.</summary>
    private readonly int _maxScale;
    private int _scale;

    public PanelPreview(int scale = 3, bool interactive = false)
    {
        _maxScale = Math.Max(1, scale);
        _scale = 1;
        _image = new Image
        {
            Source = _bitmap,
            Width = NexusCanvas.Width,
            Height = NexusCanvas.Height,
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

        // Kept only as a safety net for a window narrower than the panel's own
        // 640px, where no whole multiple fits and something has to give. Above
        // that width MeasureOverride has already sized the image to a whole
        // multiple that fits, so this never scales anything - which is the
        // point, because a Viewbox scaling by a fraction is what made the
        // preview ragged in the first place.
        Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = new Grid { Children = { _image, _overlay } },
        };
        // ⛔ NO BORDER. iCUE draws none on any surface - a vertical scan across
        // a tile edge steps #1F1F1F straight to #0D0D0D through one antialiased
        // pixel, so the fill contrast IS the edge. This had DimGray, a colour
        // that appears nowhere in the measured palette.
    }

    /// <summary>The integer factor the preview is currently drawn at.</summary>
    public int Scale => _scale;

    /// <summary>Raised when the fitted scale changes, so a caption can say what
    /// the viewer is looking at.</summary>
    public event Action<int>? ScaleChanged;

    /// <summary>
    /// ⛔ INTEGER SCALES ONLY, and this is the whole point of the override.
    ///
    /// The preview looked ragged rather than blurry, and interpolation was
    /// never the cause: it is already nearest-neighbour with aliased edges. The
    /// Image was 640 -> 1280 (2x), and then a Viewbox scaled the composed
    /// visual down to fit the card - about 0.82 in a 1111px window. Sampling
    /// therefore landed at ~1.64x: some source pixels became two device pixels
    /// and the ones beside them became one. On 10-21px text that is uneven
    /// stroke weights and dropped rows.
    ///
    /// Nearest-neighbour is only honest at whole multiples, so pick the largest
    /// whole multiple that fits and never a fraction. This is what emulators,
    /// LED-matrix editors and watch-face studios do, for exactly this reason.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width >= NexusCanvas.Width)
        {
            int want = Math.Clamp((int)(availableSize.Width / NexusCanvas.Width), 1, _maxScale);
            if (want != _scale)
            {
                _scale = want;
                _image.Width  = NexusCanvas.Width  * _scale;
                _image.Height = NexusCanvas.Height * _scale;
                _overlay.Width  = _image.Width;
                _overlay.Height = _image.Height;
                // Posted, not raised inline: a handler that writes to a caption
                // triggers layout, and raising that from inside a measure pass
                // is how a re-entrant layout loop starts.
                int at = _scale;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ScaleChanged?.Invoke(at));
            }
        }
        return base.MeasureOverride(availableSize);
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


using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NexusManager.Render;
using SkiaSharp;

namespace NexusManager.Editor;

/// <summary>
/// Choose which part of an oversized image reaches the panel, by dragging.
///
/// The whole source is drawn, dimmed, with a bright rectangle over the part that
/// will actually be shown. Drag the rectangle to move it; the wheel, or the
/// slider beside it, zooms.
///
/// Showing the WHOLE image and moving a window over it beats the other obvious
/// design - a fixed window with the image dragged behind it - because on a 640x48
/// strip the window is a 13:1 sliver. With the image behind it you can only ever
/// see the sliver, so you are panning blind through something you cannot see.
/// </summary>
public sealed class CropPicker : Control
{
    private WriteableBitmap? _bitmap;
    private int _sourceW, _sourceH;
    private BackgroundFit _fit = BackgroundFit.Cover;
    private double _zoom = 1;
    private double _focusX = 0.5, _focusY = 0.5;
    private bool _dragging;
    private Point _grabOffset;
    private string? _error;

    /// <summary>Raised while dragging, so the panel follows the finger.</summary>
    public event Action<double, double>? FocusChanged;
    public event Action<double>? ZoomChanged;

    public CropPicker()
    {
        Height = 190;
        Focusable = true;
        ClipToBounds = true;
    }

    public string? Error => _error;

    public void SetImage(string? path, BackgroundFit fit, double zoom, double focusX, double focusY)
    {
        _fit = fit; _zoom = zoom; _focusX = focusX; _focusY = focusY;

        if (path is null)
        {
            _bitmap = null; _error = null;
            InvalidateVisual();
            return;
        }

        var preview = ImagePreview.FirstFrame(path, 900, 500, out _error);
        if (preview is null) { _bitmap = null; InvalidateVisual(); return; }

        using var skia = preview.Bitmap;
        _sourceW = preview.SourceWidth;
        _sourceH = preview.SourceHeight;

        var wb = new WriteableBitmap(
            new PixelSize(skia.Width, skia.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buf = wb.Lock())
        {
            // Row by row: the Skia bitmap and the Avalonia one need not agree on
            // stride, and copying the whole block assuming they do corrupts the
            // image on any width that is not a multiple of the alignment.
            for (int y = 0; y < skia.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    skia.Bytes, y * skia.RowBytes,
                    buf.Address + y * buf.RowBytes, skia.Width * 4);
        }
        _bitmap = wb;
        InvalidateVisual();
    }

    public void SetGeometry(BackgroundFit fit, double zoom, double focusX, double focusY)
    {
        _fit = fit; _zoom = zoom; _focusX = focusX; _focusY = focusY;
        InvalidateVisual();
    }

    /// <summary>Where the source image is drawn inside this control.</summary>
    private Rect ImageRect()
    {
        if (_bitmap is null) return default;
        double bw = _bitmap.PixelSize.Width, bh = _bitmap.PixelSize.Height;
        double scale = Math.Min(Bounds.Width / bw, Bounds.Height / bh);
        double w = bw * scale, h = bh * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    /// <summary>The crop window, in control coordinates.</summary>
    private Rect CropRect()
    {
        var img = ImageRect();
        if (img.Width <= 0) return default;

        var (fw, fh) = ImagePreview.VisibleFraction(_sourceW, _sourceH, _fit, _zoom);
        double w = img.Width * fw, h = img.Height * fh;
        double x = img.X + (img.Width - w) * Math.Clamp(_focusX, 0, 1);
        double y = img.Y + (img.Height - h) * Math.Clamp(_focusY, 0, 1);
        return new Rect(x, y, w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        ctx.FillRectangle(bg, new Rect(Bounds.Size));

        if (_bitmap is null)
        {
            var text = new FormattedText(
                _error ?? "Choose an image to position it here.",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Style.TextDimBrush);
            ctx.DrawText(text, new Point(10, Bounds.Height / 2 - 8));
            return;
        }

        var img = ImageRect();
        ctx.DrawImage(_bitmap, new Rect(_bitmap.Size), img);

        // Everything outside the crop is dimmed, so the bright part is the part
        // that reaches the panel. Four rectangles rather than an even-odd path:
        // simpler to read, and one less geometry object per frame.
        var crop = CropRect();
        var shade = new SolidColorBrush(Colors.Black, 0.62);
        ctx.FillRectangle(shade, new Rect(img.X, img.Y, img.Width, crop.Y - img.Y));
        ctx.FillRectangle(shade, new Rect(img.X, crop.Bottom, img.Width, img.Bottom - crop.Bottom));
        ctx.FillRectangle(shade, new Rect(img.X, crop.Y, crop.X - img.X, crop.Height));
        ctx.FillRectangle(shade, new Rect(crop.Right, crop.Y, img.Right - crop.Right, crop.Height));

        ctx.DrawRectangle(new Pen(Style.AccentBrush, 2), crop);

        var label = new FormattedText(
            $"{_sourceW}×{_sourceH}  →  640×48",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10, Style.TextDimBrush);
        ctx.DrawText(label, new Point(6, Bounds.Height - 16));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bitmap is null) return;
        var p = e.GetPosition(this);
        var crop = CropRect();
        // Grabbing anywhere jumps the window under the pointer, which is what
        // people expect from a crop box; grabbing inside it keeps the offset so
        // the box does not snap out from under the finger.
        if (!crop.Contains(p))
        {
            MoveTo(p.X - crop.Width / 2, p.Y - crop.Height / 2);
            crop = CropRect();
        }
        _grabOffset = new Point(p.X - crop.X, p.Y - crop.Y);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _bitmap is null) return;
        var p = e.GetPosition(this);
        MoveTo(p.X - _grabOffset.X, p.Y - _grabOffset.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bitmap is null) return;
        double next = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 1.0, 12.0);
        if (Math.Abs(next - _zoom) < 0.0001) return;
        _zoom = next;
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Places the crop window's top-left at a control coordinate.</summary>
    private void MoveTo(double x, double y)
    {
        var img = ImageRect();
        var crop = CropRect();
        double slackX = img.Width - crop.Width;
        double slackY = img.Height - crop.Height;

        // No slack means nothing to choose on that axis - a stretched fit, or an
        // image already narrower than the strip. Dividing by it would be a NaN
        // that silently poisons the saved config.
        double fx = slackX > 0.5 ? Math.Clamp((x - img.X) / slackX, 0, 1) : _focusX;
        double fy = slackY > 0.5 ? Math.Clamp((y - img.Y) / slackY, 0, 1) : _focusY;

        if (Math.Abs(fx - _focusX) < 0.0001 && Math.Abs(fy - _focusY) < 0.0001) return;
        _focusX = fx; _focusY = fy;
        FocusChanged?.Invoke(_focusX, _focusY);
        InvalidateVisual();
    }
}

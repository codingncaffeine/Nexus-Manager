using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using NexusManager.Render;

namespace NexusManager.Editor;

/// <summary>
/// The interactive layer over the panel preview: cell boundaries, a selection
/// outline, and drag-to-resize.
///
/// Kept deliberately quiet. The point of the preview is to show what the panel
/// shows, so the chrome only appears when the pointer is over it - otherwise a
/// grid of handles would sit permanently on top of the thing being judged.
///
/// ⛔ A screen can legitimately have NO cells at all: an animated background with
/// nothing on top is a perfectly good screen, and two of the user's five are
/// exactly that. It must say so rather than drawing an empty area under a caption
/// promising dividers.
/// </summary>
public sealed class CellOverlay : Control, ICustomHitTest
{
    /// <summary>Grab zone around a boundary, in SCREEN pixels.</summary>
    private const double GripScreenPx = 11;

    private int _dragDivider = -1;
    private bool _hovering;

    public ScreenSpec? Screen { get; set; }
    public int SelectedIndex { get; set; } = -1;
    public int SelectedButtonIndex { get; set; } = -1;

    public event Action? Changed;
    public event Action<int>? Selected;
    public event Action<int>? ButtonSelected;

    /// <summary>Boundaries a user could actually grab. Exposed so the self
    /// test can assert the interaction exists rather than only that the control
    /// was constructed - the original bug was a resize widget that rendered
    /// nothing while a caption promised dividers.</summary>
    public int DividerCount => Math.Max(0, CellCount - 1);

    /// <summary>Readouts plus buttons on the current screen.</summary>
    /// <summary>Segments of the strip INCLUDING the leading and trailing gaps,
    /// because those are draggable too. Derived from Cells() rather than counted
    /// separately, or the two drift and the self test asserts the wrong shape.
    /// </summary>
    public int CellCount => Cells().Count;

    public CellOverlay()
    {
        // A plain Control with nothing painted does not hit-test, so Render fills a
        // transparent rect - see the first line of Render.
        ClipToBounds = true;
    }

    /// <summary>A segment of the strip. A SPACER is empty strip - the gap before
    /// the first cell or after the last - and exists so a screen with a single
    /// readout or button still has a boundary to drag. One cell has no neighbour
    /// to resize against, which is why a lone button showed no handles at all.
    /// <summary>Index values marking the two spacer cells.</summary>
    private const int SpacerLead = -1;
    private const int SpacerTrail = -2;

    private readonly record struct Cell(
        bool IsButton, int Index, double Weight, string Caption, bool IsSpacer = false);

    private List<Cell> Cells()
    {
        var cells = new List<Cell>();
        var s = Screen;
        if (s is null || s.Modules.Count + s.Buttons.Count == 0) return cells;

        // Spacers bracket the real cells, so the existing divider logic picks
        // them up with no special cases. They are added ONLY when there is
        // something to bracket: on an empty screen two spacers would present a
        // draggable boundary between two pieces of nothing.
        cells.Add(new Cell(false, SpacerLead, s.LeadWeight, "", IsSpacer: true));
        for (int i = 0; i < s.Modules.Count; i++)
            cells.Add(new Cell(false, i, s.Modules[i].Weight,
                string.IsNullOrEmpty(s.Modules[i].Label) ? s.Modules[i].Source : s.Modules[i].Label));
        for (int i = 0; i < s.Buttons.Count; i++)
            cells.Add(new Cell(true, i, s.Buttons[i].Weight,
                string.IsNullOrEmpty(s.Buttons[i].Label) ? "button" : s.Buttons[i].Label));
        cells.Add(new Cell(false, SpacerTrail, s.TrailWeight, "", IsSpacer: true));
        return cells;
    }

    private void SetWeight(Cell cell, double weight)
    {
        var s = Screen;
        if (s is null) return;
        if (cell.IsSpacer)
        {
            // Index distinguishes the two spacers. Comparing their WEIGHTS would
            // pick the wrong one whenever both happen to be equal - which is the
            // default, both zero.
            if (cell.Index == SpacerLead) s.LeadWeight = Math.Max(0, weight);
            else s.TrailWeight = Math.Max(0, weight);
            return;
        }
        if (cell.IsButton) s.Buttons[cell.Index].Weight = weight;
        else s.Modules[cell.Index].Weight = weight;
    }

    private double[] Widths(List<Cell> cells)
    {
        if (cells.Count == 0 || Bounds.Width <= 0) return [];
        double total = cells.Sum(Floor);
        if (total <= 0) return [];
        return cells.Select(c => Bounds.Width * Floor(c) / total).ToArray();
    }

    /// <summary>A spacer may be exactly zero wide; a real cell keeps a floor so
    /// it can never collapse to nothing and become unrecoverable.</summary>
    private static double Floor(Cell c) =>
        c.IsSpacer ? Math.Max(0, c.Weight) : Math.Max(0.0001, c.Weight);

    /// <summary>Minimum width in CONTROL pixels. Buttons have a lower floor than
    /// readouts: hittable and legible are different requirements.</summary>
    private double MinPx(bool isButton, bool isSpacer = false)
    {
        if (Screen is null || Bounds.Width <= 0) return 0;
        if (isSpacer) return 0;   // empty strip has no minimum
        int panelMin = isButton ? Screen.MinButtonWidth : Screen.MinModuleWidth;
        return panelMin * (Bounds.Width / NexusCanvas.Width);
    }

    public override void Render(DrawingContext ctx)
    {
        // A Control only receives pointer events where it has painted. Without
        // this the overlay would be inert whenever it draws nothing - which is
        // most of the time, since the chrome only shows on hover.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var cells = Cells();
        var typeface = new Typeface("Inter");

        if (cells.Count == 0)
        {
            if (!_hovering) return;
            // Say what is actually true instead of promising a divider that
            // cannot exist. This is the case that made the old caption a lie.
            var note = new FormattedText(
                "No readouts or buttons on this screen — add one to size it.",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, new SolidColorBrush(Colors.White, 0.75));
            ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.45), new Rect(Bounds.Size));
            ctx.DrawText(note, new Point((Bounds.Width - note.Width) / 2,
                                         (Bounds.Height - note.Height) / 2));
            return;
        }

        var widths = Widths(cells);
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x46, 0x3C));
        double x = 0;

        for (int i = 0; i < cells.Count; i++)
        {
            double w = widths[i];
            bool tooNarrow = !cells[i].IsSpacer && w < MinPx(cells[i].IsButton);
            if (cells[i].IsSpacer) { x += w; continue; }
            bool selected = cells[i].IsButton
                ? cells[i].Index == SelectedButtonIndex
                : cells[i].Index == SelectedIndex;

            var rect = new Rect(x, 0, w, Bounds.Height);
            if (selected)
                ctx.DrawRectangle(null, new Pen(Style.AccentBrush, 2), rect.Deflate(1));
            if (tooNarrow)
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0x46, 0x3C), 0.18),
                                  new Pen(red, 1.5), rect.Deflate(1));

            x += w;

            // Boundaries only while the pointer is here, or while dragging: the
            // preview's job is to show the panel, not a grid.
            if (i < cells.Count - 1 && (_hovering || _dragDivider >= 0))
            {
                bool active = i == _dragDivider;
                ctx.DrawLine(new Pen(active ? Style.AccentBrush : new SolidColorBrush(Colors.White, 0.55),
                                     active ? 2 : 1),
                             new Point(x, 0), new Point(x, Bounds.Height));
                // A grab tab, so the boundary reads as draggable rather than drawn.
                // The tab is drawn the width of the ACTUAL grab zone, so what you
                // can hit is what you can see. Drawn 6 units wide it looked - and
                // was - about four screen pixels.
                double half = Math.Max(3, Grip() * 0.45);
                var tab = new Rect(x - half, Bounds.Height / 2 - 22, half * 2, 44);
                ctx.DrawRectangle(active ? Style.AccentBrush : new SolidColorBrush(Colors.White, 0.7),
                                  null, tab, 2, 2);
            }
        }

        if (_hovering || _dragDivider >= 0) DrawWidths(ctx, cells, widths, typeface);
    }

    /// <summary>The width of each cell in PANEL pixels - the number that decides
    /// whether a readout fits, and the only one worth showing.</summary>
    private void DrawWidths(DrawingContext ctx, List<Cell> cells, double[] widths, Typeface typeface)
    {
        double x = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            double w = widths[i];
            int panelPx = (int)Math.Round(w / Bounds.Width * NexusCanvas.Width);
            bool tooNarrow = !cells[i].IsSpacer && w < MinPx(cells[i].IsButton);
            var text = new FormattedText(
                $"{panelPx}px", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10,
                tooNarrow ? new SolidColorBrush(Color.FromRgb(0xFF, 0x46, 0x3C))
                          : new SolidColorBrush(Colors.White, 0.85));
            if (cells[i].IsSpacer) { x += w; continue; }
            if (text.Width < w - 6)
            {
                var at = new Point(x + (w - text.Width) / 2, Bounds.Height - text.Height - 2);
                ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.55),
                                  new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2));
                ctx.DrawText(text, at);
            }
            x += w;
        }
    }

    /// <summary>
    /// Avalonia asks a custom-drawn control whether a point belongs to it. The
    /// default answer is derived from what was PAINTED, so an overlay that draws
    /// nothing until hover can never be hovered - a deadlock. Claim the whole
    /// rectangle instead.
    /// </summary>
    public bool HitTest(Point point) => Bounds.Contains(point + Bounds.Position);

    /// <summary>
    /// The grab zone in THIS control coordinates.
    ///
    /// ⛔ The overlay is 1280 units wide but drawn at whatever the card allows -
    /// about 0.7 of that on a 1020px window. A grip expressed in control units
    /// therefore shrinks on screen as the window narrows: 6 units was FOUR REAL
    /// PIXELS, which is why dragging read as doing nothing at all. Convert from
    /// screen pixels through the actual render transform instead.
    /// </summary>
    /// <summary>The grab zone as the user experiences it, in screen pixels.
    /// Asserted by the self test.</summary>
    public double GripScreenPixels
    {
        get
        {
            double scale = 1;
            if (TopLevel.GetTopLevel(this) is { } top
                && this.TransformToVisual(top) is { } m && m.M11 > 0.01)
                scale = m.M11;
            return Grip() * scale;
        }
    }

    private double Grip()
    {
        double scale = 1;
        if (TopLevel.GetTopLevel(this) is { } top
            && this.TransformToVisual(top) is { } m && m.M11 > 0.01)
            scale = m.M11;
        return GripScreenPx / scale;
    }

    /// <summary>Logs every pointer event the overlay receives. Set by the drag
    /// probe: reading the handlers is what let a broken one ship twice.</summary>
    public static bool Trace { get; set; }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _hovering = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hovering = false;
        if (_dragDivider < 0) Cursor = new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Trace) Console.Error.WriteLine($"[overlay] MOVED at {p} drag={_dragDivider}");
        if (_dragDivider >= 0) { DragTo(p.X); return; }
        Cursor = new Cursor(DividerAt(p.X) >= 0
            ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Trace) Console.Error.WriteLine($"[overlay] PRESSED at {p} bounds={Bounds}");
        int d = DividerAt(p.X);
        if (Trace) Console.Error.WriteLine($"[overlay]   DividerAt({p.X:0.#}) = {d}");
        if (d >= 0)
        {
            _dragDivider = d;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        var cells = Cells();
        if (cells.Count == 0) return;
        int seg = SegmentAt(p.X, cells);
        if (seg < 0) return;

        if (cells[seg].IsButton)
        {
            SelectedButtonIndex = cells[seg].Index;
            SelectedIndex = -1;
            ButtonSelected?.Invoke(cells[seg].Index);
        }
        else
        {
            SelectedIndex = cells[seg].Index;
            SelectedButtonIndex = -1;
            Selected?.Invoke(cells[seg].Index);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragDivider < 0) return;
        _dragDivider = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Changed?.Invoke();
    }

    private int DividerAt(double x)
    {
        double grip = Grip();
        var widths = Widths(Cells());
        double acc = 0;
        for (int i = 0; i < widths.Length - 1; i++)
        {
            acc += widths[i];
            if (Math.Abs(x - acc) <= grip) return i;
        }
        return -1;
    }

    /// <summary>
    /// Performs a drag without a mouse, for the self test: grab the boundary
    /// after cell <paramref name="divider"/> and move it to <paramref name="toX"/>.
    ///
    /// Exercises the same code path the pointer handlers do, so a broken drag
    /// shows up in a script instead of only under a finger.
    /// </summary>
    public bool SimulateDrag(int divider, double toX)
    {
        var cells = Cells();
        if (divider < 0 || divider + 1 >= cells.Count) return false;
        _dragDivider = divider;
        DragTo(toX);
        _dragDivider = -1;
        return true;
    }

    /// <summary>Would a press at this WINDOW point grab a boundary? Lets the
    /// probe measure the real grab tolerance instead of assuming it.</summary>
    public bool DividerNear(Point windowPoint, Visual root)
    {
        var local = root.TranslatePoint(windowPoint, this);
        return local is { } p && DividerAt(p.X) >= 0;
    }

    /// <summary>X of each grabbable boundary, in control coordinates.</summary>
    public IReadOnlyList<double> DividerPositions()
    {
        var widths = Widths(Cells());
        var xs = new List<double>();
        double acc = 0;
        for (int i = 0; i < widths.Length - 1; i++) { acc += widths[i]; xs.Add(acc); }
        return xs;
    }

    private int SegmentAt(double x, List<Cell> cells)
    {
        var widths = Widths(cells);
        double acc = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            acc += widths[i];
            if (x < acc) return i;
        }
        return widths.Length - 1;
    }

    /// <summary>
    /// Moves one boundary. Only the two adjacent cells change; everything else
    /// holds its width, which is what makes repeated adjustments predictable.
    /// </summary>
    private void DragTo(double x)
    {
        var cells = Cells();
        if (Screen is null || _dragDivider < 0 || _dragDivider + 1 >= cells.Count) return;

        var widths = Widths(cells);
        int i = _dragDivider, j = i + 1;

        double left = 0;
        for (int k = 0; k < i; k++) left += widths[k];
        double pair = widths[i] + widths[j];

        // Each side is clamped by ITS OWN minimum, so dragging a button against a
        // readout does not force the button to obey the readout's wider floor.
        double minI = MinPx(cells[i].IsButton, cells[i].IsSpacer);
        double minJ = MinPx(cells[j].IsButton, cells[j].IsSpacer);
        if (pair < minI + minJ) return;

        widths[i] = Math.Clamp(x - left, minI, pair - minJ);
        widths[j] = pair - widths[i];

        // Back to weights, preserving the total so the numbers stay in the range
        // the user has been working with rather than being renormalised to 1.
        double totalW = cells.Sum(c => Math.Max(0.0001, c.Weight));
        double totalPx = widths.Sum();
        for (int k = 0; k < cells.Count; k++)
            SetWeight(cells[k], Math.Round(widths[k] / totalPx * totalW, 4));

        InvalidateVisual();
        Changed?.Invoke();
    }
}

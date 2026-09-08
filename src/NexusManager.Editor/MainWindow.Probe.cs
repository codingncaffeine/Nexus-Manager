using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// Answers the only two questions that matter about drag-to-resize, with
    /// measurements rather than by reading the wiring:
    ///
    ///   1. would a click at a boundary actually LAND on the overlay?
    ///      (InputHitTest, which is what the real pointer path uses)
    ///   2. does dragging change the weights?
    ///
    /// Reading the code said "yes" twice while the user got nothing, so this
    /// exists to make the answer observable.
    /// </summary>
    private void RunDragProbe()
    {
        try
        {
            CellOverlay.Trace = true;
            ShowView("panel");

            // Lay the window out for real: hit testing needs arranged bounds, and
            // in tray mode nothing has arranged anything yet.
            // Lay out for REAL. A hand-rolled Measure/Arrange on an unshown window
            // produces a tree that looks arranged but is not the one the pointer
            // sees, which is how the first probe reported a point over nothing.
            Show();
            UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            // Hit testing runs against the COMPOSED scene, which does not exist
            // until a frame has actually been rendered. Probing before that
            // returns almost nothing and looks exactly like a broken overlay.
            for (int f = 0; f < 40; f++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(25);
            }
            UpdateLayout();

            var overlay = _preview.Overlay;
            Console.Out.WriteLine($"[probe] window           {Width:0}x{Height:0} at {Position} state={WindowState}");
            Console.Out.WriteLine($"[probe] screen           '{Screen.Name}'");
            Console.Out.WriteLine($"[probe] cells            {overlay.CellCount} "
                                  + $"({Screen.Modules.Count} module, {Screen.Buttons.Count} button)");
            Console.Out.WriteLine($"[probe] overlay bounds   {overlay.Bounds}");
            Console.Out.WriteLine($"[probe] visible/hit      {overlay.IsVisible}/{overlay.IsHitTestVisible} "
                                  + $"effective={overlay.IsEffectivelyVisible}");
            Console.Out.WriteLine($"[probe] rooted           {TopLevel.GetTopLevel(overlay) is not null}");

            // Where does the overlay actually sit, and does any ancestor have no
            // size or a clip that would exclude it?
            Visual? walk = overlay;
            while (walk is not null)
            {
                Console.Out.WriteLine($"[probe]   ^ {walk.GetType().Name,-22} "
                    + $"bounds={walk.Bounds} clip={(walk.ClipToBounds ? "yes" : "no")}");
                walk = Avalonia.VisualTree.VisualExtensions.GetVisualParent(walk);
            }

            var dividers = overlay.DividerPositions();
            Console.Out.WriteLine($"[probe] dividers         {dividers.Count} at "
                                  + string.Join(", ", dividers.Select(d => $"{d:0}")));

            if (dividers.Count == 0)
            {
                Console.Out.WriteLine("[probe] nothing to drag on this screen");
            }
            else
            {
                // 1. Where would a real click land?
                var local = new Point(dividers[0], overlay.Bounds.Height / 2);
                var inWindow = overlay.TranslatePoint(local, this);
                if (inWindow is null)
                {
                    Console.Out.WriteLine("[probe] could not translate into the window");
                    return;
                }
                Point wp = inWindow.Value;
                {
                    // Enumerate the WHOLE stack, not just the topmost: "not the
                    // overlay" does not say whether the overlay is absent from
                    // hit testing or merely underneath something.
                    var stack = Avalonia.VisualTree.VisualExtensions
                        .GetVisualsAt(this, wp).ToList();
                    Console.Out.WriteLine($"[probe] hit test at {wp} -> {stack.Count} visual(s)");
                    foreach (var v in stack)
                        Console.Out.WriteLine($"[probe]   {v.GetType().Name}"
                            + (ReferenceEquals(v, overlay) ? "   <== THE OVERLAY" : ""));
                    var hit = stack.FirstOrDefault();
                    Console.Out.WriteLine($"[probe] hit test at {wp}");
                    Console.Out.WriteLine($"[probe]   -> {hit?.GetType().Name ?? "NOTHING"}"
                                          + (ReferenceEquals(hit, overlay) ? "  (the overlay - good)" : "  (NOT the overlay)"));
                }

                // CONTROL: hit test something known to be interactive and
                // rendered. If THIS misses too, the probe is broken, not the
                // overlay - which is the mistake that wasted the scrolllock test.
                var ctlPoint = _moduleList.TranslatePoint(
                    new Point(_moduleList.Bounds.Width / 2, _moduleList.Bounds.Height / 2), this);
                if (ctlPoint is { } cp)
                {
                    var ctlStack = Avalonia.VisualTree.VisualExtensions
                        .GetVisualsAt(this, cp).Select(v => v.GetType().Name).Take(4).ToList();
                    Console.Out.WriteLine($"[probe] CONTROL: module list at {cp} -> "
                        + (ctlStack.Count == 0 ? "NOTHING" : string.Join(", ", ctlStack)));
                }

                // 2. Drive the REAL pointer path: press, move, release, exactly as
                //    a mouse would. SimulateDrag bypasses the handlers, so on its
                //    own it proves the maths and nothing about the plumbing.
                // How far off the boundary can a real finger be and still grab it?
                // This is the thing that was broken: the grip was 6 CONTROL units,
                // about 4 screen pixels after the Viewbox scale.
                int maxGrab = -1;
                for (int off = 0; off <= 30; off++)
                {
                    var probePt = overlay.TranslatePoint(
                        new Point(dividers[0], overlay.Bounds.Height / 2), this);
                    var offsetPt = new Point((probePt ?? wp).X + off, (probePt ?? wp).Y);
                    if (overlay.DividerNear(offsetPt, this)) maxGrab = off;
                    else break;
                }
                Console.Out.WriteLine($"[probe] grab tolerance   {maxGrab} screen px either side");

                double[] realBefore = Weights();
                try
                {
                    var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                    var props = new PointerPointProperties(
                        RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                    // Positions must be in the ROOT visual coordinates declared
                    // in the event args. Passing overlay-local points with the
                    // window as root gave (327, -144) instead of (320, 48) - the
                    // probe missing the divider, not the app.
                    var start = wp;
                    var endLocal = new Point(dividers[0] - 60, overlay.Bounds.Height / 2);
                    var end = overlay.TranslatePoint(endLocal, this) ?? wp;

                    overlay.RaiseEvent(new PointerPressedEventArgs(
                        overlay, pointer, this, start, 0, props, KeyModifiers.None));
                    overlay.RaiseEvent(new PointerEventArgs(
                        InputElement.PointerMovedEvent, overlay, pointer, this, end, 0,
                        props, KeyModifiers.None));
                    overlay.RaiseEvent(new PointerReleasedEventArgs(
                        overlay, pointer, this, end, 0,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                        KeyModifiers.None, MouseButton.Left));
                }
                catch (Exception ex)
                {
                    Console.Out.WriteLine($"[probe] raising pointer events failed: {ex.Message}");
                }
                double[] realAfter = Weights();
                bool realMoved = realBefore.Zip(realAfter).Any(p => Math.Abs(p.First - p.Second) > 0.0001);
                Console.Out.WriteLine($"[probe] REAL POINTER DRAG changed weights: {realMoved}");
                Console.Out.WriteLine($"[probe]   {string.Join(", ", realBefore.Select(w => $"{w:0.###}"))}"
                    + $"  ->  {string.Join(", ", realAfter.Select(w => $"{w:0.###}"))}");

                // 2. Does the drag change anything?
                double[] before = Weights();
                overlay.SimulateDrag(0, dividers[0] - 60);
                double[] after = Weights();
                bool moved = before.Zip(after).Any(p => Math.Abs(p.First - p.Second) > 0.0001);
                Console.Out.WriteLine($"[probe] weights before   {string.Join(", ", before.Select(w => $"{w:0.###}"))}");
                Console.Out.WriteLine($"[probe] weights after    {string.Join(", ", after.Select(w => $"{w:0.###}"))}");
                Console.Out.WriteLine($"[probe] drag changed it  {moved}");
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"[probe] FAILED: {ex}");
        }
        finally
        {
            Console.Out.Flush();
            _quitting = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            });
        }
    }

    private double[] Weights() =>
        Screen.Modules.Select(m => m.Weight)
              .Concat(Screen.Buttons.Select(b => b.Weight))
              .ToArray();
}

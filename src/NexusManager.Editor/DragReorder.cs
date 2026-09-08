using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NexusManager.Editor;

/// <summary>
/// Drag-to-reorder for tiles and group cards, which Corsair's own guide calls
/// out three times: sensors reorder by drag and drop, whole groups reorder by
/// drag and drop, and a sensor dragged OUT of a group stands on its own.
///
/// The drop target decides the insertion index from which HALF of itself the
/// pointer is over. Using the target's own index alone puts a tile dropped on
/// the right edge of its neighbour in the wrong place, which reads as the drag
/// having been ignored.
///
/// Avalonia 12 replaced IDataObject with IDataTransfer, and its
/// DoDragDropAsync takes the ORIGINAL PointerPressedEventArgs rather than the
/// move that crossed the threshold - so the press args are held until the drag
/// actually starts.
/// </summary>
public static class DragReorder
{
    /// <summary>In-process formats: these never leave the application, so there is
    /// no serialisation and no chance of another app receiving a sensor key.</summary>
    public static readonly DataFormat<string> Sensor =
        DataFormat.CreateInProcessFormat<string>("nexus-manager-sensor-key");
    public static readonly DataFormat<string> Group =
        DataFormat.CreateInProcessFormat<string>("nexus-manager-group-name");

    private const double Threshold = 4;

    /// <summary>Makes <paramref name="control"/> a drag source carrying
    /// <paramref name="key"/>.</summary>
    public static void MakeSource(Control control, DataFormat<string> format, string key)
    {
        PointerPressedEventArgs? press = null;
        Point origin = default;

        control.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
            press = e;
            origin = e.GetPosition(control);
        }, RoutingStrategies.Tunnel);

        control.AddHandler(InputElement.PointerReleasedEvent, (s, e) => press = null,
            RoutingStrategies.Tunnel);

        control.AddHandler(InputElement.PointerMovedEvent, async (s, e) =>
        {
            if (press is null) return;
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) { press = null; return; }
            var p = e.GetPosition(control);
            // A drag has to out-travel a click, or opening the three-dot menu
            // starts a drag instead.
            if (Math.Abs(p.X - origin.X) < Threshold && Math.Abs(p.Y - origin.Y) < Threshold) return;

            var started = press;
            press = null;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(format, key));

            double was = control.Opacity;
            // iCUE fades the tile being dragged rather than showing a ghost of it.
            control.Opacity = 0.45;
            try { await DragDrop.DoDragDropAsync(started, transfer, DragDropEffects.Move); }
            catch (Exception ex) { Console.Error.WriteLine($"[drag] {ex.Message}"); }
            finally { control.Opacity = was; }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Makes <paramref name="control"/> accept a drop of the same kind.
    /// <paramref name="onDrop"/> receives the dragged key and the index it should
    /// land at, already adjusted for which half of this control was hit.
    /// </summary>
    /// <param name="horizontal">true when neighbours sit side by side, so the
    /// midpoint that matters is the X one.</param>
    public static void MakeTarget(
        Control control, DataFormat<string> format, int index, bool horizontal,
        Action<string, int> onDrop)
    {
        DragDrop.SetAllowDrop(control, true);
        var original = control is Border b ? b.BorderThickness : default;

        void ClearHint()
        {
            if (control is Border bb) { bb.BorderThickness = original; bb.BorderBrush = null; }
        }

        int IndexFor(DragEventArgs e)
        {
            var p = e.GetPosition(control);
            bool after = horizontal
                ? p.X > control.Bounds.Width / 2
                : p.Y > control.Bounds.Height / 2;
            return after ? index + 1 : index;
        }

        control.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (!e.DataTransfer.Contains(format)) { e.DragEffects = DragDropEffects.None; return; }
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            if (control is Border bb)
            {
                bool after = IndexFor(e) > index;
                // A two-pixel accent edge on the side the tile will land, which
                // is the only feedback saying WHERE rather than just "droppable".
                bb.BorderBrush = Style.AccentBrush;
                bb.BorderThickness = horizontal
                    ? new Thickness(after ? 0 : 2, 0, after ? 2 : 0, 0)
                    : new Thickness(0, after ? 0 : 2, 0, after ? 2 : 0);
            }
        });

        control.AddHandler(DragDrop.DragLeaveEvent, (s, e) => ClearHint());

        control.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            ClearHint();
            if (!e.DataTransfer.Contains(format)) return;
            if (e.DataTransfer.TryGetValue(format) is not string key) return;
            e.DragEffects = DragDropEffects.Move;
            // Stops the drop bubbling to the page background, which would treat
            // an ordinary reorder as "dragged out of the group".
            e.Handled = true;
            onDrop(key, IndexFor(e));
        });
    }

    /// <summary>A drop zone that always appends, for the empty space after the
    /// last tile. Without it there is no gesture that reaches the end of a list
    /// whose last tile sits at the edge of the panel.</summary>
    public static void MakeAppendTarget(
        Control control, DataFormat<string> format, Func<int> count, Action<string, int> onDrop)
    {
        DragDrop.SetAllowDrop(control, true);
        control.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(format)
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        control.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            if (e.DataTransfer.TryGetValue(format) is not string key) return;
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            onDrop(key, count());
        });
    }

    /// <summary>
    /// A catch-all zone behind everything else. A sensor dropped here was dragged
    /// out of its group, which Corsair's guide describes as extracting a sensor
    /// from a group so it stands alone. It only ever sees drops the tiles and
    /// cards above it did not handle.
    /// </summary>
    public static void MakeExtractTarget(Control control, Action<string> onExtract)
    {
        DragDrop.SetAllowDrop(control, true);
        control.AddHandler(DragDrop.DragOverEvent, (s, e) =>
            e.DragEffects = e.DataTransfer.Contains(Sensor)
                ? DragDropEffects.Move : DragDropEffects.None);
        control.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            if (e.Handled) return;
            if (e.DataTransfer.TryGetValue(Sensor) is not string key) return;
            e.DragEffects = DragDropEffects.Move;
            onExtract(key);
        });
    }
}

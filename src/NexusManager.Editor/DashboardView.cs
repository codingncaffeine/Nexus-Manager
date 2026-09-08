using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// Every sensor the machine reports, grouped by device.
///
/// Mirrors iCUE's Dashboard, which exists for the reason their own guide gives:
/// the compact view has limited room, so a second view shows many sensors at
/// once. From that guide, this view owes the user five things - an add/remove
/// control in the top right, drag-and-drop reordering of sensors AND of whole
/// groups, dragging a sensor out of its group to stand alone, a per-group menu
/// that resizes the group, and a per-sensor menu that renames it.
/// </summary>
public sealed class DashboardView : UserControl
{
    private readonly SensorRegistry _reg;
    private readonly List<SensorTile> _tiles = [];
    // Cards stack within columns rather than wrapping in a single row. iCUE
    // does this so a short card (Radeon) does not leave a tall gap beside a
    // long one (Motherboard) - each new card goes to the shortest column.
    private readonly Grid _groups = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
    private readonly DashboardLayout _layout;

    public event Action? AddSensorRequested;

    public DashboardView(SensorRegistry reg, DashboardLayout layout)
    {
        _reg = reg;
        _layout = layout;

        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var plus = HomeView.PlusButton("Add or remove sensors");
        plus.Click += (_, _) => AddSensorRequested?.Invoke();
        Grid.SetColumn(plus, 1); head.Children.Add(plus);

        // A sensor dropped on the empty page - not on a tile or a card - was
        // dragged OUT of its group. Corsair's guide names this explicitly:
        // "extract individual sensors from groups by dragging them out". The
        // tiles mark their own drops handled, so only genuine misses land here.
        _groups.Background = Brushes.Transparent;
        DragReorder.MakeExtractTarget(_groups, key =>
        {
            if (_layout.Standalone.Contains(key)) return;
            _layout.Standalone.Add(key);
            _layout.Save(); Rebuild();
        });

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _groups,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(scroll);
        Content = dock;

        Rebuild();
    }

    /// <summary>Sensors currently shown, across every group plus the standalone
    /// cards. This is what the "+" dialog edits.</summary>
    public IEnumerable<string> VisibleKeys =>
        _reg.All.Select(s => s.Key)
            .Where(k => !IsHidden(k))
            .Concat(_layout.Standalone)
            .Distinct(StringComparer.Ordinal);

    private bool IsHidden(string key)
    {
        var desc = _reg.Describe(key);
        if (desc is null) return true;
        if (_layout.Standalone.Contains(key)) return false;
        var st = _layout.For(desc.Device);
        return st.Hidden || st.HiddenSensors.Contains(key);
    }

    /// <summary>Applies a set of visible keys from the "+" dialog, translating it
    /// back into the per-group hidden lists the layout stores.</summary>
    public void ApplyVisible(HashSet<string> visible)
    {
        foreach (var g in _reg.ByDevice())
        {
            var st = _layout.For(g.Key.Name);
            st.HiddenSensors.Clear();
            foreach (var s in g)
                if (!visible.Contains(s.Key)) st.HiddenSensors.Add(s.Key);
            // A group whose sensors are all back on screen must come back too, or
            // ticking its boxes appears to do nothing.
            if (st.Hidden && g.Any(s => visible.Contains(s.Key))) st.Hidden = false;
        }
        _layout.Standalone.RemoveAll(k => !visible.Contains(k));
        _layout.Save();
        Rebuild();
    }

    public void Rebuild()
    {
        _groups.Children.Clear();
        _tiles.Clear();

        var ordered = DashboardLayout
            .Apply(_reg.ByDevice().ToList(), _layout.Order, g => g.Key.Name)
            .ToList();

        const int Columns = 3;
        var cols = new StackPanel[Columns];
        var used = new double[Columns];
        for (int c = 0; c < Columns; c++)
        {
            cols[c] = new StackPanel { Margin = new Thickness(c == 0 ? 0 : 6, 0, 6, 0) };
            Grid.SetColumn(cols[c], c);
            _groups.Children.Add(cols[c]);
        }

        void Place(Control card, double height)
        {
            int target = 0;
            for (int c = 1; c < Columns; c++) if (used[c] < used[target]) target = c;
            cols[target].Children.Add(card);
            used[target] += height + 12;
        }

        // Sensors dragged out of their group stand alone, above the groups, as
        // iCUE shows them.
        foreach (string key in _layout.Standalone.ToList())
        {
            var desc = _reg.Describe(key);
            if (desc is null) continue;
            Place(BuildStandalone(desc), 148);
        }

        int gi = 0;
        foreach (var g in ordered)
        {
            var st = _layout.For(g.Key.Name);
            if (st.Hidden) continue;
            var list = g.Where(s => !_layout.Standalone.Contains(s.Key)).ToList();
            if (list.Count == 0) continue;

            var card = BuildGroup(g.Key, list, st, gi++);

            // Rough height: header plus one row per tile-row.
            int visible = list.Count(s => !st.HiddenSensors.Contains(s.Key));
            double h = 46 + Math.Ceiling(visible / (double)st.Columns) * (st.HideGraphs ? 74 : 100);
            Place(card, h);
        }
    }

    private Control BuildGroup(DeviceInfo dev, IReadOnlyList<SensorDescriptor> sensors, GroupState st, int groupIndex)
    {
        var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
        var shown = DashboardLayout
            .Apply(sensors.Where(s => !st.HiddenSensors.Contains(s.Key))
                          .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList(),
                   st.Order, s => s.Key)
            .ToList();

        for (int i = 0; i < shown.Count; i++)
        {
            var s = shown[i];
            var tile = new SensorTile(s, _reg.Scale)
            {
                Margin = new Thickness(0, 0, 8, 8),
                ShowChart = !st.HideGraphs && !_layout.GraphHidden(s.Key),
                DisplayLabel = _layout.NameFor(s.Key) ?? s.Label,
            };
            tile.Menu = BuildTileMenu(tile, st);

            DragReorder.MakeSource(tile, DragReorder.Sensor, s.Key);
            DragReorder.MakeTarget(tile, DragReorder.Sensor, i, horizontal: true,
                (moved, at) => ReorderInGroup(st, shown.Select(x => x.Key).ToList(), moved, at));

            _tiles.Add(tile);
            tiles.Children.Add(tile);
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        var titles = new StackPanel();
        var groupTitle = new TextBlock
        {
            Foreground = Style.TextBrush,
            FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
        };
        titles.Children.Add(groupTitle);
        TextFit.Middle(groupTitle, dev.Name.ToUpperInvariant());
        titles.Children.Add(new TextBlock
        {
            Text = CategoryNames.Display(dev.Category),
            Foreground = Style.TextDimBrush, FontSize = 11,
        });
        Grid.SetColumn(titles, 0); header.Children.Add(titles);

        var kebab = new Button
        {
            Content = "⋮", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Style.TextDimBrush, Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top, FontSize = 15,
            Flyout = GroupMenu(dev, st),
        };
        Grid.SetColumn(kebab, 1); header.Children.Add(kebab);

        var card = new Border
        {
            Background = Style.CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            // Group width is the "resize the group" control from iCUE's own
            // three-dot menu, expressed as tiles per row.
            Width = st.Columns * 202 + 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Children = { header, tiles } },
        };

        // The whole card is a drag source, but only from its header: making the
        // body draggable too would swallow every tile drag inside it.
        DragReorder.MakeSource(header, DragReorder.Group, dev.Name);
        DragReorder.MakeTarget(card, DragReorder.Group, groupIndex, horizontal: false,
            (moved, at) =>
            {
                EnsureGroupOrder();
                DashboardLayout.Move(_layout.Order, moved, at);
                _layout.Save(); Rebuild();
            });
        return card;
    }

    /// <summary>The Order list starts empty, so the first drag has nothing to
    /// reorder. Seed it with the current on-screen sequence first.</summary>
    private void EnsureGroupOrder()
    {
        if (_layout.Order.Count > 0) return;
        _layout.Order.AddRange(
            DashboardLayout.Apply(_reg.ByDevice().ToList(), _layout.Order, g => g.Key.Name)
                           .Select(g => g.Key.Name));
    }

    private void ReorderInGroup(GroupState st, List<string> current, string moved, int at)
    {
        if (st.Order.Count == 0) st.Order.AddRange(current);
        foreach (string k in current) if (!st.Order.Contains(k)) st.Order.Add(k);
        DashboardLayout.Move(st.Order, moved, at);
        _layout.Save(); Rebuild();
    }

    /// <summary>A sensor dragged out of its group, shown as its own card. The
    /// guide describes this explicitly: "extract individual sensors from groups
    /// by dragging them out".</summary>
    private Control BuildStandalone(SensorDescriptor desc)
    {
        var tile = new SensorTile(desc, _reg.Scale)
        {
            ShowChart = !_layout.GraphHidden(desc.Key),
            DisplayLabel = _layout.NameFor(desc.Key) ?? desc.Label,
        };
        tile.Menu = Style.Menu(
            (_layout.OnHome(desc.Key) ? "Remove from Home" : "Add to Home", () =>
                { _layout.SetHome(desc.Key, !_layout.OnHome(desc.Key)); _layout.Save(); Rebuild(); }),
            ("Rename", () => RenameAsync(tile)),
            (_layout.GraphHidden(desc.Key) ? "Show graph" : "Hide graph", () =>
                { _layout.SetGraph(desc.Key, _layout.GraphHidden(desc.Key)); _layout.Save(); Rebuild(); }),
            ("Return to group", () =>
                { _layout.Standalone.Remove(desc.Key); _layout.Save(); Rebuild(); }),
            ("Remove", () =>
            {
                _layout.Standalone.Remove(desc.Key);
                _layout.For(desc.Device).HiddenSensors.Add(desc.Key);
                _layout.Save(); Rebuild();
            }));
        _tiles.Add(tile);

        DragReorder.MakeSource(tile, DragReorder.Sensor, desc.Key);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        var standaloneTitle = new TextBlock
        {
            Foreground = Style.TextBrush,
            FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
        };
        TextFit.Middle(standaloneTitle, desc.Device.ToUpperInvariant());
        header.Children.Add(standaloneTitle);
        return new Border
        {
            Background = Style.CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Width = 226,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Children = { header, tile } },
        };
    }

    private MenuFlyout GroupMenu(DeviceInfo dev, GroupState st)
    {
        var items = new List<(string, Action?)>();
        foreach (int cols in new[] { 1, 2, 3, 4 })
        {
            int c = cols;
            items.Add(($"Width: {c} tile{(c > 1 ? "s" : "")} per row",
                       () => { st.Columns = c; _layout.Save(); Rebuild(); }));
        }
        items.Add(("-", null));
        items.Add((st.HideGraphs ? "Show graphs" : "Hide graphs",
                   () => { st.HideGraphs = !st.HideGraphs; _layout.Save(); Rebuild(); }));
        items.Add(("Remove this group",
                   () => { st.Hidden = true; _layout.Save(); Rebuild(); }));
        return Style.Menu(items.ToArray());
    }

    private MenuFlyout BuildTileMenu(SensorTile tile, GroupState st)
    {
        string key = tile.Descriptor.Key;
        return Style.Menu(
            (_layout.OnHome(key) ? "Remove from Home" : "Add to Home", () =>
                { _layout.SetHome(key, !_layout.OnHome(key)); _layout.Save(); Rebuild(); }),
            ("Rename", () => RenameAsync(tile)),
            (_layout.GraphHidden(key) ? "Show graph" : "Hide graph", () =>
                { _layout.SetGraph(key, _layout.GraphHidden(key)); _layout.Save(); Rebuild(); }),
            ("Show on its own", () =>
            {
                if (!_layout.Standalone.Contains(key)) _layout.Standalone.Add(key);
                _layout.Save(); Rebuild();
            }),
            ("Remove", () => { st.HiddenSensors.Add(key); _layout.Save(); Rebuild(); }));
    }

    private async void RenameAsync(SensorTile tile)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            string? name = await RenameDialog.AskAsync(w, tile.DisplayLabel, tile.Descriptor.Key);
            if (name is null) return;
            _layout.Rename(tile.Descriptor.Key, string.IsNullOrWhiteSpace(name) ? null : name);
            _layout.Save();
            Rebuild();
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the process down.
            Console.Error.WriteLine($"[dashboard] rename failed: {ex.Message}");
        }
    }

    /// <summary>Pushes fresh readings into every visible tile.</summary>
    public void Refresh()
    {
        foreach (var t in _tiles)
            t.Update(_reg.Read(t.Descriptor.Key), _reg.Scale);
    }
}

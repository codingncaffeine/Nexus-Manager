using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// Sensor chooser, grouped by device as iCUE's "Add Home Sensors" dialog does:
/// device name in bold with its category beneath, sensors nested under it. A
/// flat list of 70 keys like "hwmon.spd5118.temp1#2" is unusable for picking.
/// </summary>
public sealed class SensorPickerDialog : Window
{
    private SensorDescriptor? _chosen;

    private SensorPickerDialog(SensorRegistry reg)
    {
        Title = "Choose a sensor";
        Width = 560; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tree = new TreeView { SelectionMode = SelectionMode.Single };
        var groups = new List<Node>();

        foreach (var g in reg.ByDevice())
        {
            var node = new Node
            {
                Header = g.Key.Name,
                Sub = CategoryNames.Display(g.Key.Category),
            };
            foreach (var s in g.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                double v = reg.Read(s.Key);
                node.Children.Add(new Node
                {
                    Header = s.Label,
                    Sub = double.IsNaN(v)
                        ? s.Key
                        : $"{v:F2} {Units.Symbol(s.Unit, reg.Scale)}   ·   {s.Key}",
                    Descriptor = s,
                });
            }
            groups.Add(node);
        }

        tree.ItemsSource = groups;
        tree.ItemTemplate = new FuncTreeDataTemplate<Node>(
            (n, _) =>
            {
                var sp = new StackPanel { Margin = new Avalonia.Thickness(0, 2, 0, 2) };
                sp.Children.Add(new TextBlock
                {
                    Text = n.Header,
                    FontWeight = n.Descriptor is null ? FontWeight.Bold : FontWeight.Normal,
                });
                sp.Children.Add(new TextBlock { Text = n.Sub, Foreground = Brushes.Gray, FontSize = 11 });
                return sp;
            },
            n => n.Children);

        var ok = new Button { Content = "Select", IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        tree.SelectionChanged += (_, _) =>
        {
            _chosen = (tree.SelectedItem as Node)?.Descriptor;
            ok.IsEnabled = _chosen is not null;
        };
        ok.Click += (_, _) => Close();
        cancel.Click += (_, _) => { _chosen = null; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var dock = new DockPanel { Margin = new Avalonia.Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(tree);
        Content = dock;
    }

    public static async Task<SensorDescriptor?> PickAsync(Window owner, SensorRegistry reg)
    {
        var dlg = new SensorPickerDialog(reg);
        await dlg.ShowDialog(owner);
        return dlg._chosen;
    }

    private sealed class Node
    {
        public string Header { get; init; } = "";
        public string Sub { get; init; } = "";
        public SensorDescriptor? Descriptor { get; init; }
        public List<Node> Children { get; } = [];
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// The multi-select sensor chooser behind the "+" on Home and on the Dashboard.
/// iCUE's version is a checkbox list grouped by the device each sensor belongs
/// to, with a search box - a flat list of the ~70 keys this machine reports,
/// spelled "hwmon.spd5118.temp1#2", is not something anyone can pick from.
///
/// Distinct from <see cref="SensorPickerDialog"/>, which picks exactly one
/// sensor for a panel module. This one edits a set.
/// </summary>
public sealed class SensorSelectDialog : Window
{
    private readonly HashSet<string> _selected;
    private readonly List<(CheckBox Box, SensorDescriptor Desc)> _rows = [];
    private readonly StackPanel _list = new();
    private readonly SensorRegistry _reg;
    private readonly Func<string, string?> _nameFor;
    private bool _accepted;

    private SensorSelectDialog(
        SensorRegistry reg, IEnumerable<string> current, string title, Func<string, string?> nameFor)
    {
        _reg = reg;
        _nameFor = nameFor;
        _selected = new HashSet<string>(current, StringComparer.Ordinal);

        Title = title;
        Width = 620; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Style.PageBrush;

        var search = new TextBox
        {
            PlaceholderText = "Search sensors",
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 12,
        };
        search.TextChanged += (_, _) => Filter(search.Text ?? "");

        var scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var ok = new Button
        {
            Content = "Done", IsDefault = true,
            Background = Style.AccentBrush, Foreground = Brushes.Black,
            Padding = new Thickness(20, 6, 20, 6),
        };
        ok.Click += (_, _) => { _accepted = true; Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);

        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(search, Dock.Top); dock.Children.Add(search);
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        dock.Children.Add(scroll);
        Content = dock;

        Build();
    }

    private void Build()
    {
        _list.Children.Clear();
        _rows.Clear();

        foreach (var g in _reg.ByDevice())
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new TextBlock
            {
                Text = g.Key.Name.ToUpperInvariant(), Foreground = Style.TextBrush,
                FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
            });
            head.Children.Add(new TextBlock
            {
                Text = CategoryNames.Display(g.Key.Category),
                Foreground = Style.TextDimBrush, FontSize = 10,
            });
            section.Children.Add(head);

            foreach (var s in g.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                double v = _reg.Read(s.Key);
                string shown = _nameFor(s.Key) ?? s.Label;
                var box = new CheckBox
                {
                    IsChecked = _selected.Contains(s.Key),
                    Foreground = Style.TextBrush,
                    FontSize = 12,
                    Padding = new Thickness(8, 0, 0, 0),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = shown, Foreground = Style.TextBrush, FontSize = 12 },
                            new TextBlock
                            {
                                Text = double.IsNaN(v) ? "" : $"{v:F2} {Units.Symbol(s.Unit, _reg.Scale)}",
                                Foreground = Style.TextDimBrush, FontSize = 11,
                            },
                        },
                    },
                };
                var key = s.Key;
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) _selected.Add(key);
                    else _selected.Remove(key);
                };
                _rows.Add((box, s));
                section.Children.Add(box);
            }
            _list.Children.Add(section);
        }
    }

    private void Filter(string text)
    {
        string q = text.Trim();
        foreach (var (box, desc) in _rows)
        {
            bool hit = q.Length == 0
                || desc.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                || desc.Device.Contains(q, StringComparison.OrdinalIgnoreCase)
                || desc.Key.Contains(q, StringComparison.OrdinalIgnoreCase);
            box.IsVisible = hit;
        }
        // Hide a device heading whose sensors have all been filtered out, or the
        // list reads as a column of empty headings.
        foreach (var child in _list.Children)
            if (child is StackPanel section)
                section.IsVisible = section.Children.Skip(1).Any(c => c.IsVisible);
    }

    /// <summary>Returns the chosen keys, or null if the dialog was cancelled.</summary>
    public static async Task<HashSet<string>?> EditAsync(
        Window owner, SensorRegistry reg, IEnumerable<string> current,
        string title, Func<string, string?> nameFor)
    {
        var dlg = new SensorSelectDialog(reg, current, title, nameFor);
        await dlg.ShowDialog(owner);
        return dlg._accepted ? dlg._selected : null;
    }
}

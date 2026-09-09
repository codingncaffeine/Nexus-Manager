using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexusManager.Editor;

/// <summary>
/// Picks one key for a macro step: a search box over <see cref="KeyCatalog"/>.
///
/// A flyout rather than a dialog because it is chosen from a chip that is 24
/// units tall - a modal for one value would take four clicks to set a key, and
/// a macro is mostly keys.
/// </summary>
public static class KeyPickerFlyout
{
    public static void Show(Control anchor, string current, Action<string> pick)
    {
        var search = new TextBox
        {
            PlaceholderText = "search",
            Margin = new Thickness(0, 0, 0, 6),
        };

        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = list,
            Height = 300,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel
            {
                Width = 250,
                Children = { search, scroll },
            },
        };

        void Choose(string name)
        {
            // ⛔ Hidden BEFORE the pick. Choosing a key rebuilds the
            // row list, which destroys the chip this flyout is anchored
            // to - hiding afterwards would be dismissing a popup whose
            // anchor has already left the tree.
            flyout.Hide();
            pick(name);
        }

        void Fill()
        {
            list.Children.Clear();
            string q = (search.Text ?? "").Trim();
            string? group = null;
            int shown = 0;
            foreach (var entry in KeyCatalog.All)
            {
                if (q.Length > 0 && !entry.Matches(q)) continue;
                if (entry.Group != group)
                {
                    group = entry.Group;
                    list.Children.Add(Style.Section(group));
                }
                list.Children.Add(Row(entry, current, Choose));
                shown++;
            }
            if (shown == 0)
                list.Children.Add(Style.Note($"No key matches “{q}”."));
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            string q = (search.Text ?? "").Trim();
            var first = KeyCatalog.All.FirstOrDefault(x => q.Length == 0 || x.Matches(q));
            if (first is not null) { e.Handled = true; Choose(first.Name); }
        };
        // The search box is the point of the flyout, so it takes focus. Posted
        // rather than called: the presenter is not in the tree yet when Opened
        // fires and a Focus() there is dropped.
        flyout.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => search.Focus(), Avalonia.Threading.DispatcherPriority.Input);

        Fill();
        flyout.ShowAt(anchor);
    }

    private static Control Row(KeyCatalog.Entry entry, string current, Action<string> choose)
    {
        bool on = string.Equals(entry.Name, current, StringComparison.OrdinalIgnoreCase);
        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = on ? Style.TextBrush : MacroStyle.ChipTextBrush,
            FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(name);
        if (entry.Aliases.Count > 0)
        {
            var alias = new TextBlock
            {
                Text = string.Join(" · ", entry.Aliases),
                Foreground = Style.TextDimBrush,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(alias, 1);
            grid.Children.Add(alias);
        }

        var row = new Border
        {
            Background = on ? MacroStyle.RowOnBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius(MacroStyle.Radius),
            Padding = new Thickness(8, 4, 8, 4),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerPressed += (_, e) => { e.Handled = true; choose(entry.Name); };
        row.PointerEntered += (_, _) => { if (!on) row.Background = MacroStyle.ChipBrush; };
        row.PointerExited  += (_, _) => { if (!on) row.Background = Brushes.Transparent; };
        return row;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Actions;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private readonly ListBox _btnList = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        MaxHeight = 120,
    };
    private int _btnIndex;

    private ButtonSpec? SelectedButton =>
        Screen.Buttons.Count == 0 ? null
        : Screen.Buttons[Math.Clamp(_btnIndex, 0, Screen.Buttons.Count - 1)];

    /// <summary>
    /// Touch buttons for this screen, appended to the screen property column.
    ///
    /// Kept separate from the module list on purpose. Modules and buttons DO
    /// share one weight budget across the strip, so a combined list would model
    /// that more honestly - but the module list has already produced two
    /// re-entrancy loops and a double-parent crash, and widening its index
    /// semantics is not worth reopening that.
    /// </summary>
    private void BuildButtonSection()
    {
        _themeProps.Children.Add(Head("Buttons"));
        _themeProps.Children.Add(Style.Note(
            "Buttons share the strip with the readouts above, by weight. "
            + "Tap one on the panel to fire it."));

        Building(() =>
        {
            _btnList.ItemsSource = Screen.Buttons
                .Select((b, i) => $"{i + 1}. {(string.IsNullOrEmpty(b.Label) ? "(no label)" : b.Label)}  ·  {b.Action}")
                .ToList();
            _btnList.SelectedIndex = Screen.Buttons.Count == 0
                ? -1 : Math.Clamp(_btnIndex, 0, Screen.Buttons.Count - 1);
        });
        // Keep the strip highlight in step with the list, so the two views of
        // the same selection never disagree.
        _preview.SelectedButtonIndex = Screen.Buttons.Count == 0 ? -1 : _btnIndex;
        _preview.InvalidateVisual();
        _themeProps.Children.Add(_btnList);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        row.Children.Add(Flat("+", "Add a button", () =>
        {
            Screen.Buttons.Add(new ButtonSpec
            {
                Label = "Button",
                Action = new SystemAction { Kind = ActionKind.Volume, Target = "up", Amount = 5 },
                Icon = ButtonIcon.VolumeUp,
            });
            _btnIndex = Screen.Buttons.Count - 1;
            Changed(); RefreshThemePanel();
        }));
        row.Children.Add(Flat("−", "Remove this button", () =>
        {
            if (Screen.Buttons.Count == 0) return;
            Screen.Buttons.RemoveAt(Math.Clamp(_btnIndex, 0, Screen.Buttons.Count - 1));
            _btnIndex = Math.Max(0, _btnIndex - 1);
            Changed(); RefreshThemePanel();
        }));
        row.Children.Add(Flat("◀", "Move left", () => MoveButton(-1)));
        row.Children.Add(Flat("▶", "Move right", () => MoveButton(+1)));
        _themeProps.Children.Add(row);

        var b = SelectedButton;
        if (b is null)
        {
            _themeProps.Children.Add(Style.Note("No buttons on this screen yet."));
            return;
        }

        _themeProps.Children.Add(Row("Label", Text(b.Label, v =>
            { b.Label = v; Changed(); RefreshButtonListLabels(); })));
        _themeProps.Children.Add(Row("Weight", Num(b.Weight, v =>
            { b.Weight = Math.Clamp(v, 0.1, 12); Changed(); })));

        var icon = new ComboBox
        {
            ItemsSource = Enum.GetValues<ButtonIcon>().ToList(), SelectedItem = b.Icon,
        };
        icon.SelectionChanged += (_, _) =>
        {
            if (_building || icon.SelectedItem is not ButtonIcon v) return;
            b.Icon = v; Changed();
        };
        _themeProps.Children.Add(Row("Icon", icon));
        _themeProps.Children.Add(Row("Text", Colour(b.Color, v => b.Color = v)));
        _themeProps.Children.Add(Row("Fill", Colour(b.Background, v => b.Background = v)));
        _themeProps.Children.Add(Row("Outline", Colour(b.Border, v => b.Border = v)));

        // --- what it does ------------------------------------------------------
        _themeProps.Children.Add(Head("Does"));
        var kind = new ComboBox
        {
            ItemsSource = Enum.GetValues<ActionKind>().ToList(), SelectedItem = b.Action.Kind,
        };
        kind.SelectionChanged += (_, _) =>
        {
            if (_building || kind.SelectedItem is not ActionKind v) return;
            b.Action.Kind = v;
            // A sensible target for the new kind, so switching does not leave the
            // button pointing at something meaningless from the previous one.
            b.Action.Target = v switch
            {
                ActionKind.Volume => "up",
                ActionKind.Media => "play-pause",
                ActionKind.Brightness => "up",
                ActionKind.Screen => _set.Screens.FirstOrDefault(s => s != Screen)?.Name ?? "",
                _ => "",
            };
            Changed(); RefreshThemePanel();
        };
        _themeProps.Children.Add(Row("Action", kind));

        switch (b.Action.Kind)
        {
            case ActionKind.Volume:
            case ActionKind.Brightness:
                // ⛔ Every action edit must refresh the list entry. Changing the
                // direction used to update NOTHING visible - the list still read
                // "volume up", so a button renamed "Volume Down" that still did
                // volume up looked identical to one that worked.
                _themeProps.Children.Add(Row("Direction", Choice(
                    b.Action.Kind == ActionKind.Volume ? ["up", "down", "mute"] : ["up", "down"],
                    b.Action.Target, v => { b.Action.Target = v; Changed(); RefreshButtonListLabels(); })));
                _themeProps.Children.Add(Row("Step %", Num(b.Action.Amount, v =>
                    { b.Action.Amount = Math.Clamp(v, 1, 50); Changed(); RefreshButtonListLabels(); })));
                break;

            case ActionKind.Media:
                _themeProps.Children.Add(Row("Transport", Choice(
                    ["play-pause", "next", "previous", "stop"],
                    b.Action.Target, v => { b.Action.Target = v; Changed(); RefreshButtonListLabels(); })));
                _themeProps.Children.Add(Style.Note(
                    "Sent over MPRIS to whichever player is running, so it reaches the "
                    + "actual application rather than relying on media keys."));
                break;

            case ActionKind.Screen:
                _themeProps.Children.Add(Row("Screen", Choice(
                    _set.Screens.Select(s => s.Name).ToArray(),
                    b.Action.Target, v => { b.Action.Target = v; Changed(); RefreshButtonListLabels(); })));
                break;

            case ActionKind.Launch:
                _themeProps.Children.Add(Row("Command", Text(b.Action.Target, v =>
                    { b.Action.Target = v; Changed(); RefreshButtonListLabels(); })));
                _themeProps.Children.Add(Style.Note(
                    "Run through a shell, so pipes and arguments work as typed."));
                break;

            case ActionKind.Key:
                _themeProps.Children.Add(Row("Keys", Text(b.Action.Target, v =>
                    { b.Action.Target = v; Changed(); RefreshButtonListLabels(); })));
                _themeProps.Children.Add(Style.Note(
                    "A key name, or several joined with + for a chord, e.g. ctrl+alt+t. "
                    + "Sent through a virtual keyboard, which is the only way on Wayland."));
                break;
        }
    }

    /// <summary>A dropdown over a fixed set of strings.</summary>
    private Control Choice(string[] options, string? current, Action<string> set)
    {
        var box = new ComboBox { ItemsSource = options.ToList(), SelectedItem = current };
        box.SelectionChanged += (_, _) =>
        {
            if (_building || box.SelectedItem is not string v) return;
            set(v);
        };
        return box;
    }

    private void RefreshButtonListLabels()
    {
        // Same trap as the module list: assigning ItemsSource raises
        // SelectionChanged, which would rebuild the panel that raised it.
        Building(() =>
        {
            int keep = _btnList.SelectedIndex;
            _btnList.ItemsSource = Screen.Buttons
                .Select((b, i) => $"{i + 1}. {(string.IsNullOrEmpty(b.Label) ? "(no label)" : b.Label)}  ·  {b.Action}")
                .ToList();
            _btnList.SelectedIndex = keep;
        });
    }

    private void MoveButton(int delta)
    {
        int from = Math.Clamp(_btnIndex, 0, Screen.Buttons.Count - 1);
        int to = from + delta;
        if (Screen.Buttons.Count < 2 || to < 0 || to >= Screen.Buttons.Count) return;
        (Screen.Buttons[from], Screen.Buttons[to]) = (Screen.Buttons[to], Screen.Buttons[from]);
        _btnIndex = to;
        Changed(); RefreshThemePanel();
    }

    private void WireButtonList()
    {
        if (_btnListWired) return;
        _btnListWired = true;
        _btnList.SelectionChanged += (_, _) =>
        {
            if (_building || _btnList.SelectedIndex < 0) return;
            _btnIndex = _btnList.SelectedIndex;
            RefreshThemePanel();
        };
    }
    private bool _btnListWired;
}

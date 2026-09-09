using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using NexusManager.Actions;

namespace NexusManager.Editor;

/// <summary>
/// One event in the macro list, drawn to iCUE's own geometry (MacroStyle).
///
/// Number, kebab, then chips: what KIND of event, its VALUE, and for a key,
/// its STATE. Clicking the value chip edits it in place - a key opens the
/// picker, a delay turns into a text box - and clicking the state chip cycles
/// tap -> hold -> release. There is no second editing surface to keep in step:
/// the context menu's "Edit Event" invokes these same handlers.
/// </summary>
public sealed class MacroRow : Border
{
    /// <summary>What the row needs from the dialog that owns it.</summary>
    public sealed record Hooks(
        Action Select,
        Action<Control> Menu,
        Action Changed,
        Action<Control, MacroStep> PickKey);

    private readonly MacroStep _step;
    private readonly Hooks _hooks;
    private readonly Border _value;

    public MacroRow(int number, MacroStep step, bool selected, Hooks hooks)
    {
        _step = step;
        _hooks = hooks;

        Height = MacroStyle.RowHeight;
        Margin = new Thickness(0, 0, 0, MacroStyle.RowGap);
        // ⛔ Transparent, not null. A Border with no background is not hit
        // tested, so clicking the empty space right of the chips would select
        // nothing and the row would feel dead everywhere but on a chip.
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        var band = new Border
        {
            Width = MacroStyle.NumberBand,
            Background = selected ? MacroStyle.RowOnBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius(MacroStyle.Radius),
        };
        var inside = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{MacroStyle.NumberCol},*"),
        };
        var num = new TextBlock
        {
            Text = number.ToString(),
            Foreground = Style.TextDimBrush,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        inside.Children.Add(num);

        var kebab = new Border
        {
            Background = Brushes.Transparent,
            Width = 16,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = MacroGlyph.Kebab(),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        kebab.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _hooks.Select();
            _hooks.Menu(kebab);
        };
        Grid.SetColumn(kebab, 1);
        inside.Children.Add(kebab);
        band.Child = inside;

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = MacroStyle.ChipGap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chips.Children.Add(band);

        IBrush fill = selected ? MacroStyle.RowOnBrush : MacroStyle.ChipBrush;
        chips.Children.Add(MacroStyle.ChipBox(MacroGlyph.Type(step.Kind), fill));

        _value = MacroStyle.ChipBox(ValueContent(step), fill);
        _value.Cursor = new Cursor(StandardCursorType.Hand);
        _value.PointerPressed += (_, e) => { e.Handled = true; _hooks.Select(); EditValue(); };
        ToolTip.SetTip(_value, step.Kind == MacroStepKind.Delay
            ? "Click to set the wait, in milliseconds"
            : "Click to choose the key");
        chips.Children.Add(_value);

        if (step.Kind != MacroStepKind.Delay)
        {
            var state = MacroStyle.ChipBox(StateContent(step.Kind), fill);
            state.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(state, "Click to cycle tap → hold → release");
            state.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _hooks.Select();
                step.Kind = step.Kind switch
                {
                    MacroStepKind.Tap => MacroStepKind.KeyDown,
                    MacroStepKind.KeyDown => MacroStepKind.KeyUp,
                    _ => MacroStepKind.Tap,
                };
                _hooks.Changed();
            };
            chips.Children.Add(state);
        }

        Child = chips;

        PointerPressed += (_, e) =>
        {
            _hooks.Select();
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                _hooks.Menu(this);
            }
        };
    }


    /// <summary>
    /// What this row actually DREW, read back out of its own tree: the number,
    /// then every chip's text in the order they appear.
    ///
    /// ⛔ Deliberately not built from the MacroStep. A probe that formats the
    /// model and compares it to itself is a tautology - it would pass against a
    /// row that rendered nothing at all. This walks the controls.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var t in this.GetLogicalDescendants().OfType<TextBlock>())
        {
            string? text = t.Text;
            if (string.IsNullOrEmpty(text) && t.Inlines is { Count: > 0 } runs)
                text = string.Concat(runs.OfType<Run>().Select(r => r.Text));
            text = text?.Trim();
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return string.Join(" ", parts);
    }
    /// <summary>Opens whichever editor this row's value wants. Also what the
    /// context menu's "Edit Event" calls, so the two routes cannot drift.</summary>
    public void EditValue()
    {
        if (_step.Kind == MacroStepKind.Delay) EditDelay();
        else _hooks.PickKey(_value, _step);
    }

    /// <summary>
    /// A delay chip becomes a text box in place. Committed on Enter or on
    /// losing focus, and clamped to the same 0..60000 the runner clamps to -
    /// both, because a config file is an input too.
    /// </summary>
    private void EditDelay()
    {
        var box = new TextBox
        {
            Text = _step.DelayMs.ToString(),
            Width = 56,
            Height = MacroStyle.RowHeight,
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = MacroStyle.ChipTextBrush,
        };
        bool committed = false;
        void Commit()
        {
            if (committed) return;
            committed = true;
            if (int.TryParse(box.Text, out int ms))
                _step.DelayMs = Math.Clamp(ms, 0, 60_000);
            _hooks.Changed();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; committed = true; _hooks.Changed(); }
        };
        box.LostFocus += (_, _) => Commit();

        _value.Child = box;
        box.Focus();
        box.SelectAll();
    }

    private static Control ValueContent(MacroStep step)
    {
        if (step.Kind == MacroStepKind.Delay)
        {
            // Number then a DIMMER unit, in one chip, as iCUE draws "400 ms".
            // Two runs rather than two chips: the unit is part of the value.
            var t = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            t.Inlines = [
                new Run { Text = step.DelayMs.ToString(), Foreground = MacroStyle.ChipTextBrush },
                new Run { Text = " ms", Foreground = Style.TextDimBrush },
            ];
            return t;
        }

        bool unset = string.IsNullOrWhiteSpace(step.Key);
        var label = MacroStyle.ChipLabel(unset ? "set key…" : KeyCatalog.Display(step.Key));
        if (unset) label.Foreground = Style.TextDimBrush;
        else if (UinputKeyboard.Resolve(step.Key) is null)
        {
            // A name the runner cannot resolve stops the WHOLE macro before a
            // single key is sent, so the row has to say so rather than looking
            // like every other row.
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            ToolTip.SetTip(label, $"'{step.Key}' is not a key this can send");
        }
        return label;
    }

    private static Control StateContent(MacroStepKind kind)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(MacroGlyph.State(kind));
        row.Children.Add(MacroStyle.ChipLabel(kind switch
        {
            MacroStepKind.KeyDown => "hold",
            MacroStepKind.KeyUp => "release",
            _ => "tap",
        }));
        return row;
    }
}

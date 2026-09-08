using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexusManager.Editor;

/// <summary>Renames a sensor to something meaningful, showing the underlying key
/// so it stays clear which reading is being renamed.</summary>
public sealed class RenameDialog : Window
{
    private string? _result;
    private readonly TextBox _box;

    private RenameDialog(string current, string sensorKey)
    {
        Title = "Rename sensor";
        Width = 420; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Style.PageBrush;
        CanResize = false;

        _box = new TextBox { Text = current, Margin = new Thickness(0, 6, 0, 0) };

        var ok = new Button { Content = "Save", IsDefault = true };
        ok.Click += (_, _) => { _result = _box.Text ?? ""; Close(); };
        var reset = new Button { Content = "Use original" };
        reset.Click += (_, _) => { _result = ""; Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => { _result = null; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(reset); buttons.Children.Add(ok); buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Display name", Foreground = Style.TextBrush, FontSize = 12 },
                _box,
                new TextBlock
                {
                    Text = sensorKey, Foreground = Style.TextDimBrush, FontSize = 10,
                    Margin = new Thickness(0, 6, 0, 0),
                },
                buttons,
            },
        };
    }

    public static async Task<string?> AskAsync(Window owner, string current, string sensorKey)
    {
        var d = new RenameDialog(current, sensorKey);
        await d.ShowDialog(owner);
        return d._result;
    }
}

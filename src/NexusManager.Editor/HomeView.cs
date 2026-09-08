using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// iCUE's Home screen, which is the view their guide describes first and the one
/// the application actually opens on.
///
/// The annotated diagram on Corsair's guide page splits it into four regions:
/// the menu bar (1), the profile rail (2), a fixed-width column holding Murals
/// and Sensors (3), and the connected-device area (4). This reproduces 3 and 4;
/// the shell owns 1 and 2.
///
/// Murals are keyboard lighting scenes and have no meaning here, so region 3
/// carries the Sensors section alone - a hand-picked shortlist, distinct from the
/// Dashboard's everything-grouped-by-device. Region 4 holds one card, for the
/// panel this application exists to drive.
/// </summary>
public sealed class HomeView : UserControl
{
    private readonly SensorRegistry _reg;
    private readonly DashboardLayout _layout;
    private readonly List<SensorTile> _tiles = [];
    private readonly WrapPanel _sensors = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _column = new();

    /// <summary>The panel card's live view. Owned here so it has exactly one
    /// logical parent - a control added to two parents throws in Avalonia, and
    /// that is what crashed the Panel tab in the first version.</summary>
    public PanelPreview Preview { get; } = new(scale: 1);

    private readonly TextBlock _deviceStatus = new()
    {
        Foreground = Style.TextDimBrush, FontSize = 11,
    };

    /// <summary>Raised when the user asks to configure the panel, so the shell can
    /// switch views.</summary>
    public event Action? ConfigureRequested;
    public event Action? AddSensorRequested;

    public HomeView(SensorRegistry reg, DashboardLayout layout)
    {
        _reg = reg;
        _layout = layout;

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("372,*") };

        // --- region 3: the fixed-width sensor column ---------------------------
        var colScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _column,
            Padding = new Thickness(16, 14, 16, 16),
        };
        var colBorder = new Border
        {
            Child = colScroll,
            // iCUE rules a hairline down the inside edge of this column rather
            // than tinting it a different shade.
            BorderBrush = Style.TopBarBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        Grid.SetColumn(colBorder, 0); root.Children.Add(colBorder);

        // --- region 4: connected devices ---------------------------------------
        var devices = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 14, 0, 0) };
        devices.Children.Add(BuildPanelCard());
        var devScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = devices,
        };
        Grid.SetColumn(devScroll, 1); root.Children.Add(devScroll);

        Content = root;
        Rebuild();
    }

    /// <summary>Shows the panel's connection state on the device card, the way
    /// iCUE shows battery and DPI on a mouse card.</summary>
    public void SetDeviceStatus(string text) => _deviceStatus.Text = text;

    public void Rebuild()
    {
        _column.Children.Clear();
        _sensors.Children.Clear();
        _tiles.Clear();

        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var title = new TextBlock
        {
            Text = "Sensors", Foreground = Style.TextMidBrush, FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0); head.Children.Add(title);

        var plus = PlusButton("Add or remove Home sensors");
        plus.Click += (_, _) => AddSensorRequested?.Invoke();
        Grid.SetColumn(plus, 1); head.Children.Add(plus);
        _column.Children.Add(head);

        foreach (string key in _layout.Home.ToList())
        {
            var desc = _reg.Describe(key);
            // A sensor pinned on a machine that no longer reports it must not
            // strand the entry; skip it but leave it in the list, so plugging the
            // device back in restores the tile.
            if (desc is null) continue;

            var tile = new SensorTile(desc, _reg.Scale, TileVariant.Home)
            {
                Margin = new Thickness(0, 0, 8, 8),
                Width = 162,
                ShowChart = !_layout.GraphHidden(key),
                DisplayLabel = _layout.NameFor(key) ?? desc.Label,
            };
            tile.Menu = BuildTileMenu(tile);

            int index = _tiles.Count;
            DragReorder.MakeSource(tile, DragReorder.Sensor, key);
            DragReorder.MakeTarget(tile, DragReorder.Sensor, index, horizontal: true,
                (moved, at) =>
                {
                    DashboardLayout.Move(_layout.Home, moved, at);
                    _layout.Save(); Rebuild();
                });

            _tiles.Add(tile);
            _sensors.Children.Add(tile);
        }

        if (_tiles.Count == 0)
            _column.Children.Add(new TextBlock
            {
                Text = "No sensors pinned yet. Use + to choose the readings you want here, "
                     + "or add one from the Dashboard.",
                Foreground = Style.TextDimBrush, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

        _column.Children.Add(_sensors);

        // Dropping below the last tile appends. Without this there is no gesture
        // that reaches the end of the list.
        var tail = new Border { Height = 40, Background = Brushes.Transparent };
        DragReorder.MakeAppendTarget(tail, DragReorder.Sensor, () => _layout.Home.Count,
            (moved, at) => { DashboardLayout.Move(_layout.Home, moved, at); _layout.Save(); Rebuild(); });
        _column.Children.Add(tail);
    }

    private MenuFlyout BuildTileMenu(SensorTile tile)
    {
        string key = tile.Descriptor.Key;
        return Style.Menu(
            ("Remove from Home", () => { _layout.SetHome(key, false); _layout.Save(); Rebuild(); }),
            ("Rename", async () =>
            {
                if (TopLevel.GetTopLevel(this) is not Window w) return;
                string? name = await RenameDialog.AskAsync(w, tile.DisplayLabel, key);
                if (name is null) return;
                _layout.Rename(key, string.IsNullOrWhiteSpace(name) ? null : name);
                _layout.Save(); Rebuild();
            }),
            (_layout.GraphHidden(key) ? "Show graph" : "Hide graph", () =>
            {
                _layout.SetGraph(key, _layout.GraphHidden(key));
                _layout.Save(); Rebuild();
            }));
    }

    internal static Button PlusButton(string tip) => new()
    {
        Content = "+",
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Style.TextMidBrush,
        FontSize = 20,
        Padding = new Thickness(8, 0, 4, 2),
        VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = tip,
    };

    /// <summary>
    /// The device card. iCUE fills region 4 with one card per connected product,
    /// each showing the device name, a gear, and a picture of the hardware. Ours
    /// shows the panel's actual output, which is a truer picture than a render.
    /// </summary>
    private Control BuildPanelCard()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = "ICUE NEXUS", Foreground = Style.TextBrush,
            FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
        });
        var gear = new Button
        {
            Content = "⚙", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Style.TextDimBrush, FontSize = 14, Padding = new Thickness(6, 0, 0, 0),
            [ToolTip.TipProperty] = "Configure the panel",
        };
        gear.Click += (_, _) => ConfigureRequested?.Invoke();
        Grid.SetColumn(gear, 1); head.Children.Add(gear);

        Preview.HorizontalAlignment = HorizontalAlignment.Center;
        Preview.VerticalAlignment = VerticalAlignment.Center;

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(0, 26, 0, 0) };
        body.Children.Add(Preview);
        body.Children.Add(_deviceStatus);

        return new Border
        {
            Background = Style.CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14, 16, 18),
            Margin = new Thickness(0, 0, 14, 14),
            Width = 690,
            Child = new StackPanel { Children = { head, body } },
        };
    }

    /// <summary>Pushes fresh readings into every visible tile.</summary>
    public void Refresh()
    {
        foreach (var t in _tiles)
            t.Update(_reg.Read(t.Descriptor.Key), _reg.Scale);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private readonly ContentControl _viewHost = new();
    private readonly StackPanel _nav = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 2,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private Control? _panelEditor;
    private DashboardView? _dashboard;
    private HomeView? _home;
    private string _view = App.StartView ?? "home";

    /// <summary>Panel connection indicator in the rail: the one piece of state
    /// worth seeing from any view.</summary>
    private readonly Border _panelBadge = new()
    {
        Width = 40, Height = 40, CornerRadius = new CornerRadius(6),
        Background = Style.TileBrush,
        BorderBrush = Style.AccentBrush, BorderThickness = new Thickness(2),
    };

    /// <summary>
    /// The three views. iCUE has Home, Dashboard and Murals; Murals are keyboard
    /// lighting scenes with no meaning on a 640x48 strip, so the third slot is
    /// the Panel editor - the thing this application exists for.
    ///
    /// Corsair's own guide is explicit about why Home and Dashboard both exist:
    /// the compact view runs out of room, so a second view shows many sensors at
    /// once.
    /// </summary>
    private Control BuildShell()
    {
        // Chrome measured off iCUE's own window: a PURE BLACK top bar, a left rail
        // LIGHTER than the content ground, and cards darker than the ground they
        // sit on. Two of those three are the opposite of the obvious guess.
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Height = 44,
            Background = Style.TopBarBrush,
        };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 18, 0),
        };
        brand.Children.Add(new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(5),
            Background = Style.AccentBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        brand.Children.Add(new TextBlock
        {
            Text = "NEXUS MANAGER", Foreground = Style.TextBrush, FontSize = 12,
            FontWeight = FontWeight.SemiBold, LetterSpacing = 1.4,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(brand, 0); bar.Children.Add(brand);

        _nav.Children.Add(NavTab("Home", "home"));
        _nav.Children.Add(NavTab("Dashboard", "dashboard"));
        _nav.Children.Add(NavTab("Panel", "panel"));
        _nav.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_nav, 1); bar.Children.Add(_nav);

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        _liveToDevice.Foreground = Style.TextDimBrush;
        _liveToDevice.FontSize = 11;
        tools.Children.Add(_liveToDevice);
        tools.Children.Add(_unitToggle);
        // iCUE puts its settings gear at the far right of the menu bar, and
        // sensor logging lives behind it. Same place, same reason.
        tools.Children.Add(BarButton("⚙", "Settings", OpenSettings));
        Grid.SetColumn(tools, 3); bar.Children.Add(tools);

        Grid.SetColumnSpan(bar, 2);
        Grid.SetRow(bar, 0); root.Children.Add(bar);

        // Left rail. iCUE keeps profile switches here; ours holds the panel
        // connection indicator and will hold profiles when they exist.
        var railStack = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0), Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        railStack.Children.Add(RailButton("›", "Expand", () => { }));
        railStack.Children.Add(_panelBadge);
        railStack.Children.Add(RailButton("+", "Add a profile", () => { }));

        var rail = new Border
        {
            Width = 62,
            Background = Style.RailBrush,
            Child = railStack,
        };
        Grid.SetRow(rail, 1); Grid.SetColumn(rail, 0); root.Children.Add(rail);

        Grid.SetRow(_viewHost, 1); Grid.SetColumn(_viewHost, 1);
        root.Children.Add(_viewHost);

        return root;
    }

    private static Button BarButton(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Style.TextDimBrush,
            FontSize = 15,
            Padding = new Thickness(8, 2, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button RailButton(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 40, Height = 34,
            Background = Style.TileBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Foreground = Style.TextDimBrush,
            FontSize = 15,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button NavTab(string text, string id)
    {
        var b = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Style.TextDimBrush,
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(0),
            FontSize = 13,
        };
        b.Click += (_, _) => ShowView(id);
        b.Tag = id;
        return b;
    }

    private void ShowView(string id)
    {
        _view = id;
        foreach (var child in _nav.Children)
        {
            if (child is not Button b) continue;
            bool on = (string?)b.Tag == id;
            // iCUE marks the active tab with weight and brightness alone - no
            // underline, no pill, nothing behind the text.
            b.Foreground = on ? Style.TextBrush : Style.TextDimBrush;
            b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        }

        if (!_ready) { _viewHost.Content = Waiting(); return; }

        switch (id)
        {
            case "home":
                _viewHost.Content = EnsureHome();
                break;
            case "dashboard":
                _viewHost.Content = EnsureDashboard();
                break;
            default:
                // Cached even when it throws. BuildPanelEditor parents shared
                // controls, so a partial failure leaves them attached; retrying
                // would re-add them and turn one fault into a permanent one.
                if (_panelEditor is null)
                {
                    try { _panelEditor = BuildPanelEditor(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[view] panel editor failed: {ex}");
                        _panelEditor = new TextBlock
                        {
                            Text = $"The panel editor could not be built:\n{ex.Message}",
                            Foreground = Style.TextDimBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                        };
                        _viewHost.Content = _panelEditor;
                        throw;
                    }
                }
                _viewHost.Content = _panelEditor;
                break;
        }
    }

    private HomeView EnsureHome()
    {
        if (_home is not null) return _home;
        _home = new HomeView(_reg!, _dashLayout);
        _home.ConfigureRequested += () => ShowView("panel");
        _home.AddSensorRequested += async () =>
        {
            var chosen = await SensorSelectDialog.EditAsync(
                this, _reg!, _dashLayout.Home, "Home sensors", _dashLayout.NameFor);
            if (chosen is null) return;
            // Keep the existing order and append anything newly ticked, so
            // editing the set does not silently reshuffle the ones already there.
            _dashLayout.Home.RemoveAll(k => !chosen.Contains(k));
            foreach (string k in chosen) if (!_dashLayout.Home.Contains(k)) _dashLayout.Home.Add(k);
            _dashLayout.Save();
            _home!.Rebuild();
        };
        return _home;
    }

    private DashboardView EnsureDashboard()
    {
        if (_dashboard is not null) return _dashboard;
        _dashboard = new DashboardView(_reg!, _dashLayout);
        _dashboard.AddSensorRequested += async () =>
        {
            var chosen = await SensorSelectDialog.EditAsync(
                this, _reg!, _dashboard!.VisibleKeys, "Dashboard sensors", _dashLayout.NameFor);
            if (chosen is not null) _dashboard.ApplyVisible(chosen);
        };
        return _dashboard;
    }

    private async void OpenSettings()
    {
        try
        {
            await SettingsWindow.ShowAsync(this, _reg!, _dashLayout, _settings, _logger!,
                b => { try { _device?.SetBrightness(b); } catch (Exception) { } });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] {ex.Message}"); }
    }

    private static Control Waiting() => new TextBlock
    {
        Text = "Discovering sensors…",
        Foreground = Style.TextDimBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

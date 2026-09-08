using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexusManager.Editor;

/// <summary>
/// One place for colour and type.
///
/// These are MEASURED off iCUE's own dashboard, not chosen by eye. Every surface
/// value below is the modal colour of a flat region in
/// _reference/icue/icue_dashboard_001, sampled with a histogram over a crop of
/// several thousand pixels. Two of them are counter-intuitive and were wrong
/// while they were guessed:
///
///   - the left RAIL is the LIGHTEST surface in the window (#3A3A3A), not the
///     darkest. It reads as a raised edge, not a recess.
///   - a group card is DARKER than the page it sits on (#1F1F1F on #2C2C2C),
///     and a sensor tile is darker again (#0D0D0D). Depth goes inward, so the
///     numbers sit at the bottom of a well.
///
/// A vertical scan across a tile edge shows #1F1F1F stepping to #0D0D0D through
/// ONE antialiased pixel: iCUE draws no border on a tile. The fill contrast is
/// the whole edge. Do not add a stroke back.
/// </summary>
public static class Style
{
    // --- measured surfaces, darkest to lightest ------------------------------
    /// <summary>Top bar. Pure black, measured - not a near-black.</summary>
    public static readonly Color TopBar    = Color.FromRgb(0x00, 0x00, 0x00);
    /// <summary>A sensor tile: the deepest surface, where readings live.</summary>
    public static readonly Color Tile      = Color.FromRgb(0x0D, 0x0D, 0x0D);
    /// <summary>A group card, holding tiles.</summary>
    public static readonly Color Card      = Color.FromRgb(0x1F, 0x1F, 0x1F);
    /// <summary>The content ground the cards sit on.</summary>
    public static readonly Color Page      = Color.FromRgb(0x2C, 0x2C, 0x2C);
    /// <summary>The left rail - lighter than the page.</summary>
    public static readonly Color Rail      = Color.FromRgb(0x3A, 0x3A, 0x3A);

    /// <summary>Hairline for the few places iCUE does use one (rail edge, field
    /// borders). Never on a sensor tile.</summary>
    public static readonly Color Line      = Color.FromRgb(0x45, 0x45, 0x45);

    public static readonly Color Text      = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color TextDim   = Color.FromRgb(0x87, 0x90, 0x92);
    public static readonly Color TextMid   = Color.FromRgb(0x98, 0x98, 0x98);
    /// <summary>iCUE's yellow, measured off the selected profile tile.</summary>
    public static readonly Color Accent    = Color.FromRgb(0xEA, 0xE4, 0x50);
    public static readonly Color Selected  = Color.FromRgb(0x3A, 0x3A, 0x3A);

    /// <summary>iCUE's context menus are a WHITE popup with dark text - the one
    /// light surface in the whole window. Distinctive enough that matching the
    /// dark theme instead reads as a different application.</summary>
    public static readonly Color MenuBack  = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color MenuText  = Color.FromRgb(0x1A, 0x1A, 0x1A);

    public static readonly IBrush TopBarBrush    = new SolidColorBrush(TopBar);
    public static readonly IBrush PageBrush      = new SolidColorBrush(Page);
    public static readonly IBrush CardBrush      = new SolidColorBrush(Card);
    public static readonly IBrush TileBrush      = new SolidColorBrush(Tile);
    public static readonly IBrush RailBrush      = new SolidColorBrush(Rail);
    public static readonly IBrush LineBrush      = new SolidColorBrush(Line);
    public static readonly IBrush TextBrush      = new SolidColorBrush(Text);
    public static readonly IBrush TextDimBrush   = new SolidColorBrush(TextDim);
    public static readonly IBrush TextMidBrush   = new SolidColorBrush(TextMid);
    public static readonly IBrush AccentBrush    = new SolidColorBrush(Accent);
    public static readonly IBrush MenuBackBrush  = new SolidColorBrush(MenuBack);
    public static readonly IBrush MenuTextBrush  = new SolidColorBrush(MenuText);

    /// <summary>A rounded panel, as iCUE groups its sensor tiles.</summary>
    public static Border CardPanel(string heading, string sub, Control body) => new()
    {
        Background = CardBrush,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10, 12, 12),
        Child = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Head(heading, sub),
                body,
            },
        },
    };

    private static Control Head(string heading, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(new TextBlock
        {
            Text = heading.ToUpperInvariant(),
            Foreground = TextBrush,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            // iCUE letter-spaces its group headers; it is most of why they read
            // as headers rather than just larger text.
            LetterSpacing = 0.8,
        });
        if (!string.IsNullOrEmpty(sub))
            sp.Children.Add(new TextBlock { Text = sub, Foreground = TextDimBrush, FontSize = 11 });
        DockPanel.SetDock(sp, Dock.Top);
        return sp;
    }

    public static TextBlock Label(string t) => new()
    {
        Text = t, Foreground = TextDimBrush, FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Note(string t) => new()
    {
        Text = t, Foreground = TextDimBrush, FontSize = 10,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
    };

    public static TextBlock Section(string t) => new()
    {
        Text = t.ToUpperInvariant(), Foreground = TextDimBrush,
        FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
        Margin = new Thickness(0, 14, 0, 6),
    };

    /// <summary>
    /// A flyout styled the way iCUE styles its three-dot menus: white card, dark
    /// text, generous row height. Pass ("-", null) for a separator.
    /// </summary>
    public static MenuFlyout Menu(params (string Header, Action? OnClick)[] items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (header, onClick) in items)
        {
            if (header == "-") { flyout.Items.Add(new Separator()); continue; }
            var item = new MenuItem
            {
                Header = header,
                Foreground = MenuTextBrush,
                Background = Brushes.Transparent,
                FontSize = 13,
                Padding = new Thickness(10, 7, 22, 7),
            };
            if (onClick is { } act) item.Click += (_, _) => act();
            flyout.Items.Add(item);
        }
        flyout.FlyoutPresenterClasses.Add("icue-menu");
        return flyout;
    }
}

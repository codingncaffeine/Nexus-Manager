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

    /// <summary>
    /// One entry in a menu that carries an accelerator column, or is greyed.
    ///
    /// iCUE's row menu shows `Ctrl+C` and `Del` right-aligned beside the verb,
    /// and greys the two Paste entries when the clipboard is empty - which is
    /// how the menu says "there is nothing to paste" without a dialog.
    /// </summary>
    public readonly record struct MenuEntry(
        string Header, Action? OnClick, string? Accel = null, bool Enabled = true)
    {
        /// <summary>A separator row.</summary>
        public static readonly MenuEntry Separator = new("-", null);
    }

    /// <summary>
    /// The same white iCUE popup, with an accelerator column and per-entry
    /// enabling.
    ///
    /// The accelerator is rendered as part of the header rather than through
    /// MenuItem.InputGesture: the gesture text is drawn by a named part of
    /// Fluent's template whose foreground is tuned for a DARK menu, and it
    /// would land near-white on this white card. Building the two-column
    /// header here depends on no template part at all.
    /// </summary>
    public static MenuFlyout Menu(params MenuEntry[] items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var entry in items)
        {
            if (entry.Header == "-") { flyout.Items.Add(new Separator()); continue; }

            // MinWidth rather than a stretch alignment on the MenuItem: the
            // header presenter sizes to its content, so without a floor the
            // accelerator would sit hard against the verb instead of in a
            // column of its own.
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                MinWidth = 148,
            };
            var verb = new TextBlock
            {
                Text = entry.Header,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(verb, 0);
            header.Children.Add(verb);
            if (!string.IsNullOrEmpty(entry.Accel))
            {
                var accel = new TextBlock
                {
                    Text = entry.Accel,
                    Opacity = 0.55,
                    Margin = new Thickness(28, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(accel, 1);
                header.Children.Add(accel);
            }

            var item = new MenuItem
            {
                Header = header,
                Foreground = MenuTextBrush,
                Background = Brushes.Transparent,
                FontSize = 13,
                Padding = new Thickness(10, 7, 22, 7),
                IsEnabled = entry.Enabled,
            };
            if (entry.OnClick is { } act) item.Click += (_, _) => act();
            flyout.Items.Add(item);
        }
        flyout.FlyoutPresenterClasses.Add("icue-menu");
        return flyout;
    }
}

/// <summary>
/// The macro editor's own surfaces and geometry.
///
/// Measured off _reference/icue/macros/iCUE_Macro.png (1928x1080) by pixel
/// scan, not by eye. Kept in one place so the dialog reads as layout rather
/// than as arithmetic, and so no number has to be re-derived from the
/// screenshot a second time.
///
/// A horizontal scan across the SELECTED row (y=880) settles how selection
/// works, which is not guessable from looking:
///
///   848      well border, 1px #4B4B4B
///   849-855  well padding, 7px
///   856-911  #4B4B4B  - the number+kebab band
///   912-919  #2A2A2A  - gap, NOT covered by the selection
///   920-943  #4B4B4B  - chip 1 (it is #1F1F1F on an unselected row)
///   944-951  gap ... and so on, 24px chips on a 32px pitch
///
/// So selecting a row does NOT paint one continuous bar: the number band
/// lights up and each chip's own fill lifts to the same value, with the gaps
/// staying well-coloured. Painting a single band instead is visibly wrong.
/// </summary>
public static class MacroStyle
{
    // --- surfaces ------------------------------------------------------------
    /// <summary>The macro card, darker than the page it sits on - the same
    /// inward-going depth order as the dashboard's tiles.</summary>
    public static readonly Color Card       = Color.FromRgb(0x13, 0x13, 0x13);
    /// <summary>The event list well.</summary>
    public static readonly Color Well       = Color.FromRgb(0x2B, 0x2B, 0x2B);
    /// <summary>1px, and the ONLY border in this surface. Chips and rows have
    /// none - fill contrast is their whole edge.</summary>
    public static readonly Color WellBorder = Color.FromRgb(0x4B, 0x4B, 0x4B);
    public static readonly Color Chip       = Color.FromRgb(0x1F, 0x1F, 0x1F);
    public static readonly Color ChipText   = Color.FromRgb(0xC0, 0xC0, 0xC0);
    /// <summary>Selected row: the number band, and every chip on it.</summary>
    public static readonly Color RowOn      = Color.FromRgb(0x4B, 0x4B, 0x4B);
    public static readonly Color TabTrack   = Color.FromRgb(0x2B, 0x2B, 0x2B);
    public static readonly Color TabOn      = Color.FromRgb(0x4B, 0x4B, 0x4B);
    public static readonly Color TabOnText  = Color.FromRgb(0xEC, 0xEC, 0xEC);
    public static readonly Color TabOffText = Color.FromRgb(0x77, 0x77, 0x77);

    public static readonly IBrush CardBrush       = new SolidColorBrush(Card);
    public static readonly IBrush WellBrush       = new SolidColorBrush(Well);
    public static readonly IBrush WellBorderBrush = new SolidColorBrush(WellBorder);
    public static readonly IBrush ChipBrush       = new SolidColorBrush(Chip);
    public static readonly IBrush ChipTextBrush   = new SolidColorBrush(ChipText);
    public static readonly IBrush RowOnBrush      = new SolidColorBrush(RowOn);
    public static readonly IBrush TabTrackBrush   = new SolidColorBrush(TabTrack);
    public static readonly IBrush TabOnBrush      = new SolidColorBrush(TabOn);
    public static readonly IBrush TabOnTextBrush  = new SolidColorBrush(TabOnText);
    public static readonly IBrush TabOffTextBrush = new SolidColorBrush(TabOffText);

    // --- geometry, in device-independent units -------------------------------
    // ⛔ Units, never pixels on a Canvas. These were measured at 100% scale and
    // a hit target expressed in a scaled control's own units is not what the
    // finger meets - the trap that made a 6-unit grab zone 4 real px.
    /// <summary>Row height. The pitch is this plus <see cref="RowGap"/>.</summary>
    public const double RowHeight = 24;
    public const double RowGap    = 4;
    /// <summary>Between the well's border and the first row.</summary>
    public const double WellPad   = 7;
    public const double ChipSize  = 24;
    public const double ChipGap   = 8;
    /// <summary>The number+kebab band: 56 units, of which the number takes 30.</summary>
    public const double NumberBand = 56;
    public const double NumberCol  = 30;
    public const double Radius     = 3;

    /// <summary>
    /// One chip. iCUE's chips are square by default and GROW to fit their
    /// content - the `Q` chip is 24 wide, the `400 ms` chip is 46 - so this is
    /// a minimum, not a size.
    /// </summary>
    public static Border ChipBox(Control content, IBrush? fill = null) => new()
    {
        Background = fill ?? ChipBrush,
        CornerRadius = new CornerRadius(Radius),
        Height = ChipSize,
        MinWidth = ChipSize,
        Padding = new Thickness(6, 0, 6, 0),
        Child = content,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>Text inside a chip, at the measured weight and colour.</summary>
    public static TextBlock ChipLabel(string text) => new()
    {
        Text = text,
        Foreground = ChipTextBrush,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// iCUE's segmented tab strip: a #2B2B2B track, the selected segment raised
    /// to #4B4B4B with near-white text, 1px dividers between segments.
    ///
    /// ⛔ It is NOT white-with-dark-text, which is what it looks like at a
    /// glance and what a first reading of the same screenshot claimed. The one
    /// place iCUE inverts is the context menu.
    /// </summary>
    public static Control Segmented(string[] labels, int selected, Action<int> pick, double segment = 110)
    {
        var track = new Border
        {
            Background = TabTrackBrush,
            CornerRadius = new CornerRadius(Radius),
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0)
                row.Children.Add(new Border
                {
                    Width = 1, Background = WellBorderBrush,
                    Margin = new Thickness(0, 4, 0, 4),
                });
            int index = i;
            bool on = i == selected;
            var seg = new Border
            {
                Background = on ? TabOnBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(
                    i == 0 ? Radius : 0, i == labels.Length - 1 ? Radius : 0,
                    i == labels.Length - 1 ? Radius : 0, i == 0 ? Radius : 0),
                Width = segment,
                Child = new TextBlock
                {
                    Text = labels[i],
                    Foreground = on ? TabOnTextBrush : TabOffTextBrush,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            seg.PointerPressed += (_, e) => { e.Handled = true; pick(index); };
            row.Children.Add(seg);
        }
        track.Child = row;
        return track;
    }
}

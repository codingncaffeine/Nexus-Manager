using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using NexusManager.Device;
using AvStyle = Avalonia.Styling.Style;

namespace NexusManager.Editor;

/// <summary>
/// Application set up entirely in code rather than App.axaml. The XAML variant
/// silently produced a window with no content: without a theme, controls have no
/// templates and draw nothing, so the failure looks like a transparent window
/// rather than an error. Declaring the theme here cannot fail that way.
/// </summary>
public sealed class App : Application
{
    /// <summary>Set from the command line (--tray) or from settings, so login
    /// can bring the panel up without throwing a window at the user.</summary>
    public static bool StartHidden { get; set; }

    /// <summary>Opening view (--view home|dashboard|panel). Exists so every view
    /// can be BUILT without a click - the Panel tab once threw on first open, and
    /// a crash that needs a mouse to reproduce cannot be checked from a script.
    /// </summary>
    public static string? StartView { get; set; }

    /// <summary>Build every view once, report, and exit. See MainWindow.SelfTest.</summary>
    public static bool SelfTest { get; set; }

    /// <summary>Measure whether drag-to-resize actually works (--probe-drag).</summary>
    public static bool ProbeDrag { get; set; }
    public static bool ProbeVisualizer { get; set; }

    /// <summary>
    /// True for diagnostics that must leave the user's configuration exactly as
    /// they found it. Autosave is suppressed for the whole run; see
    /// MainWindow.QueueAutosave. ⛔ --probe-action is NOT in this set, because
    /// whether an edit reaches the file is precisely what it measures.
    /// </summary>
    public static bool ReadOnly { get; set; }

    /// <summary>Measure whether the macro editor renders, commits, and can be
    /// REACHED from the Action dropdown (--probe-macro). The last of those is
    /// what was actually broken.</summary>
    public static bool ProbeMacro { get; set; }

    /// <summary>An optional .cuescreens pack to render a real imported macro
    /// from. The official packs live outside the repository, so this is never
    /// required - the probe builds the same shapes in code.</summary>
    public static string? ProbeMacroPack { get; set; }

    /// <summary>Measure whether a button's action edit reaches the model and the
    /// file (--probe-action). Answers the last open question about the Volume
    /// buttons with a measurement rather than by reading the wiring.</summary>
    public static bool ProbeAction { get; set; }

    /// <summary>Run the UI without opening the panel (--no-device). Lets the
    /// editor be soaked or profiled while a real instance owns the hardware,
    /// instead of having to stop the one that is actually driving the display.
    /// </summary>
    public static bool NoDevice { get; set; }

    /// <summary>The panel ownership lock this process holds. Released on exit, and
    /// by the kernel if the process dies some other way.</summary>
    public static SingleInstance? Instance { get; set; }
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // The colour wheel lives in a separate package that ships its OWN control
        // themes, and Fluent does not pull them in. Without this the
        // ColorSpectrum applies no template and draws an empty box - silently,
        // which is the same class of failure that made App.axaml unusable here.
        // --selftest asserts the template applied, so this cannot rot unnoticed.
        Styles.Add(new StyleInclude(new Uri("avares://nexus-manager-editor/"))
        {
            Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml"),
        });
        RequestedThemeVariant = ThemeVariant.Dark;
        AddMenuStyles();
    }

    /// <summary>
    /// iCUE's three-dot menus are a WHITE popup with dark text, and it is one of
    /// the most recognisable things about the interface - every other surface is
    /// near-black. Fluent's dark flyout would read as a different application, so
    /// the presenter and its items are restyled here rather than per call site.
    /// </summary>
    private void AddMenuStyles()
    {
        var presenter = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu"));
        presenter.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Style.MenuBackBrush));
        presenter.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)));
        presenter.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0, 6, 0, 6)));
        presenter.Setters.Add(new Setter(Layoutable.MinWidthProperty, 168d));
        Styles.Add(presenter);

        // The item background has to be beaten down in every visual state or
        // Fluent paints its own dark hover over the white card.
        var item = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu")
                                     .Descendant().OfType<MenuItem>());
        item.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        item.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Style.MenuTextBrush));
        Styles.Add(item);

        var hover = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu")
                                      .Descendant().OfType<MenuItem>().Class(":pointerover")
                                      .Template().OfType<Border>().Name("PART_LayoutRoot"));
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))));
        Styles.Add(hover);

        var normal = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu")
                                       .Descendant().OfType<MenuItem>()
                                       .Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter"));
        normal.Setters.Add(new Setter(TextBlock.ForegroundProperty, Style.MenuTextBrush));
        Styles.Add(normal);

        // A greyed entry has to READ as greyed on a WHITE card. Fluent's
        // disabled foreground is tuned for a dark menu and lands nearly
        // invisible here, which would make "Paste Below" with an empty
        // clipboard look like an entry that just does nothing when clicked.
        var off = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu")
                                    .Descendant().OfType<MenuItem>().Class(":disabled"));
        off.Setters.Add(new Setter(TemplatedControl.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8))));
        Styles.Add(off);

        var offHeader = new AvStyle(x => x.OfType<FlyoutPresenter>().Class("icue-menu")
                                          .Descendant().OfType<MenuItem>().Class(":disabled")
                                          .Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter"));
        offHeader.Setters.Add(new Setter(TextBlock.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8))));
        Styles.Add(offHeader);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the last window must not tear the process down: this app
            // lives in the tray and keeps the panel alive after the window is
            // dismissed. Shutdown is explicit, from the tray menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            // A window that opens hidden still has to run: the panel it drives is
            // physical, and a blank strip reads as broken hardware. StartHidden is
            // honoured after construction so the render loop is already wired.
            if (StartHidden || window.PrefersHidden) window.Hide();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

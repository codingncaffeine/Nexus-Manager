using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace NexusManager.Editor;

/// <summary>
/// Application set up entirely in code rather than App.axaml. The XAML variant
/// silently produced a window with no content: without a theme, controls have no
/// templates and draw nothing, so the failure looks like a transparent window
/// rather than an error. Declaring the theme here cannot fail that way.
/// </summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

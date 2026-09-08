using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// The tray icon, and with it the reason this application keeps running after
    /// its window is dismissed.
    ///
    /// The panel is a physical display bolted to the user's keyboard. If the
    /// process exits it goes blank, and a blank strip looks like broken hardware
    /// rather than a closed application. So the window is a view onto a service
    /// that outlives it: closing hides, the tray keeps rendering, and only "Quit"
    /// actually stops - at which point the panel IS blanked, deliberately, so it
    /// never shows a frozen frame of stale numbers.
    /// </summary>
    private void BuildTray()
    {
        if (_tray is not null) return;
        try
        {
            var menu = new NativeMenu();

            var open = new NativeMenuItem("Open Nexus Manager");
            open.Click += (_, _) => ShowWindow();
            menu.Add(open);

            menu.Add(new NativeMenuItemSeparator());

            var live = new NativeMenuItem("Panel output")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _liveToDevice.IsChecked == true,
            };
            live.Click += (_, _) =>
            {
                _liveToDevice.IsChecked = _liveToDevice.IsChecked != true;
                live.IsChecked = _liveToDevice.IsChecked == true;
                // Turning output off should not leave the last frame frozen on
                // the glass - blank it, so "off" looks off.
                if (_liveToDevice.IsChecked != true)
                    try { _device?.Blank(); } catch (Exception) { }
            };
            menu.Add(live);

            menu.Add(new NativeMenuItemSeparator());

            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => QuitFully();
            menu.Add(quit);

            _tray = new TrayIcon
            {
                ToolTipText = "Nexus Manager",
                Menu = menu,
                IsVisible = true,
            };
            try
            {
                using var s = AssetLoader.Open(
                    new Uri("avares://nexus-manager-editor/Assets/icon.png"));
                _tray.Icon = new WindowIcon(s);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tray] icon unavailable: {ex.Message}");
            }

            _tray.Clicked += (_, _) => ShowWindow();

            // Avalonia binds tray icons per Application, not per window.
            if (Avalonia.Application.Current is { } app)
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            // A desktop with no StatusNotifier host still has to run the panel.
            Console.Error.WriteLine($"[tray] unavailable: {ex.Message}");
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitFully()
    {
        _quitting = true;
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

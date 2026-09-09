using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// Debounces the screen-config save.
    ///
    /// Every edit used to need the Save button, so a resize you were happy with
    /// was lost the moment the window closed - which is the same complaint as
    /// having to re-size the window each launch. Dragging fires continuously, so
    /// the write is coalesced rather than done per pixel.
    /// </summary>
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private bool _autosaveWired;

    private void WireAutosave()
    {
        if (_autosaveWired) return;
        _autosaveWired = true;
        _autosave.Tick += async (_, _) =>
        {
            _autosave.Stop();
            await SaveScreensAsync();
        };
    }

    /// <summary>Called after any edit that changes what the panel draws.</summary>
    private void QueueAutosave()
    {
        // ⛔ A READ-ONLY DIAGNOSTIC MUST NOT WRITE THE USER'S CONFIG.
        //
        // The probes deliberately mutate the model to measure the editor and
        // put it back afterwards - but edits AUTOSAVE, and the panel rebuild
        // that follows a restore raises a TextChanged of its own once the
        // _building guard has dropped. --probe-macro re-armed this timer that
        // way AFTER it had stopped it, and screens.json was rewritten on the
        // way out. The content happened to be identical; that it was written
        // at all is the fault. --probe-action is deliberately NOT read-only:
        // reaching the file is the thing it measures.
        if (App.ReadOnly) return;
        if (!_ready || _building) return;
        WireAutosave();
        _autosave.Stop();      // restart the window on every keystroke or drag pixel
        _autosave.Start();
    }

    private async Task SaveScreensAsync()
    {
        try
        {
            await Config.SaveAsync(_set, null);
            _status.Text = $"saved · {Config.Path}";
        }
        catch (Exception ex)
        {
            _status.Text = $"save failed: {ex.Message}";
        }
    }

    // --- window geometry ------------------------------------------------------

    /// <summary>
    /// Restores the last window size and position.
    ///
    /// ⛔ A saved position can point at a monitor that is no longer attached, so
    /// it is only honoured when it lands on a screen that currently exists -
    /// otherwise the window opens somewhere the user cannot reach it.
    /// </summary>
    private void RestoreGeometry()
    {
        try
        {
            if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
            {
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;
            }

            if (_settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue)
            {
                var wanted = new PixelPoint(_settings.WindowX, _settings.WindowY);
                if (Screens.All.Any(s => s.Bounds.Contains(wanted)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = wanted;
                }
            }

            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[geometry] restore failed: {ex.Message}");
        }
    }

    private void SaveGeometry()
    {
        try
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            // Only record the size when NOT maximised, or the restored-down size
            // is lost and un-maximising gives a full-screen-sized window.
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.WindowX = Position.X;
                _settings.WindowY = Position.Y;
            }
            _settings.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[geometry] save failed: {ex.Message}");
        }
    }
}

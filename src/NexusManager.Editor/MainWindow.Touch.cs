using Avalonia.Threading;
using NexusManager.Device;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private HidSharp.HidStream? _touchStream;
    private CancellationTokenSource? _touchCts;

    /// <summary>
    /// Swipe-to-change-screen, on the panel itself.
    ///
    /// This existed only in the headless `nexus-manager daemon`. When the tray
    /// application became the thing that owns the panel, gestures silently
    /// stopped working - the editor pushed frames but never opened the touch
    /// stream at all, so the hardware reported swipes to nobody.
    ///
    /// Touch runs on its own HID stream: a 104 Hz input poll must never queue
    /// behind the 121 output writes each frame costs.
    /// </summary>
    private void StartTouch()
    {
        if (_touchStream is not null) return;
        try
        {
            _touchStream = NexusDevice.OpenTouchStream();
            var touch = new NexusTouch(_touchStream);
            touch.Gesture += OnPanelGesture;
            _touchCts = new CancellationTokenSource();
            _ = touch.RunAsync(_touchCts.Token);
            Console.Error.WriteLine("[touch] gestures active");
        }
        catch (Exception ex)
        {
            // Frames still go out; only swiping is lost. Say so rather than
            // leaving the panel silently unresponsive to touch.
            Console.Error.WriteLine($"[touch] unavailable ({ex.Message}); swipe will not work.");
            StopTouch();
        }
    }

    private void StopTouch()
    {
        try { _touchCts?.Cancel(); } catch (Exception) { }
        try { _touchStream?.Dispose(); } catch (Exception) { }
        _touchCts?.Dispose();
        _touchCts = null;
        _touchStream = null;
    }

    /// <summary>
    /// Arrives on the touch reader's thread. Everything it touches - the screen
    /// index, the render state, the list selection - belongs to the UI thread.
    /// </summary>
    private void OnPanelGesture(Gesture g)
    {
        if (g.Kind == GestureKind.Tap)
        {
            Dispatcher.UIThread.Post(() => OnPanelTap(g));
            return;
        }
        if (g.Kind is not (GestureKind.SwipeLeft or GestureKind.SwipeRight)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_ready || _set.Screens.Count < 2) return;

            // Content moves left => next screen, which is the direction Corsair's
            // own screens move and what the hand expects.
            int delta = g.Kind == GestureKind.SwipeLeft ? +1 : -1;
            int n = _set.Screens.Count;
            _screenIndex = _set.WrapScreens
                ? (_screenIndex + delta % n + n) % n
                : Math.Clamp(_screenIndex + delta, 0, n - 1);
            _moduleIndex = 0;

            RebuildScreenState();
            // Keep the editor's own screen list in step, so what the window shows
            // selected is what the panel is showing.
            Building(() =>
            {
                if (_screenIndex < _screenList.ItemCount) _screenList.SelectedIndex = _screenIndex;
            });
            RefreshModuleList();
            RefreshThemePanel();
        });
    }
}

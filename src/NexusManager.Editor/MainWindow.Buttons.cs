using System.Diagnostics;
using NexusManager.Actions;
using NexusManager.Device;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>Button rects for the screen currently on the panel.</summary>
    private IReadOnlyList<ButtonLayout> _buttonLayout = [];

    private readonly ActionRunner _actions = new();

    /// <summary>Which button is flashing, and until when. The panel has no travel
    /// and no click, so the flash is the only confirmation a press registered.</summary>
    private int _pressedButton = -1;
    private TimeSpan _flashUntil;

    private void WireActions()
    {
        _actions.GoToScreen = name =>
        {
            int i = _set.Screens.FindIndex(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _screenIndex = i;
                _moduleIndex = 0;
                RebuildScreenState();
                Building(() =>
                {
                    if (_screenIndex < _screenList.ItemCount) _screenList.SelectedIndex = _screenIndex;
                });
                RefreshModuleList();
                RefreshThemePanel();
            });
            return true;
        };

        _actions.AdjustBrightness = delta =>
        {
            _settings.Brightness = (int)Math.Clamp(_settings.Brightness + delta, 0, 100);
            try { _device?.SetBrightness(_settings.Brightness); } catch (Exception) { }
            _settings.Save();
        };
    }

    /// <summary>
    /// A tap on the panel. X is the only axis the hardware reports, which is
    /// enough: buttons are full-height cells, so a hit test is a range check.
    /// </summary>
    private void OnPanelTap(Gesture g)
    {
        var buttons = _buttonLayout;
        if (buttons.Count == 0) return;

        // The END of the touch, not the start: a tap that drifts a few pixels
        // should act where the finger left, which is what the eye tracked.
        int hit = ScreenLayout.HitTest(buttons, g.EndX);
        if (hit < 0) return;

        _pressedButton = hit;
        _flashUntil = _clock.Elapsed + ButtonRenderer.FlashDuration;
        var action = buttons[hit].Spec.Action;
        Console.Error.WriteLine($"[button] x={g.EndX} -> [{hit}] '{buttons[hit].Spec.Label}' {action}");
        _actions.Run(action);
        if (_actions.LastError is { } err) Console.Error.WriteLine($"[button] {err}");
    }
}

using System.Diagnostics;
using NexusManager.Actions;
using NexusManager.Device;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Cli;

/// <summary>
/// Drives the panel: renders the current screen, samples sensors on a slow tick,
/// and moves between screens on a swipe.
/// </summary>
public sealed class Daemon : IDisposable
{
    /// <summary>Not readonly: a replugged panel is a NEW handle, and a service
    /// that cannot take one has to be restarted by hand after every unplug.</summary>
    private NexusDevice _dev;
    private readonly SensorRegistry _reg;
    private readonly ScreenSet _set;

    private readonly List<List<ModuleLayout>> _layouts = [];
    private readonly List<List<ButtonLayout>> _buttonLayouts = [];
    private readonly List<List<VisualizerLayout>> _visualLayouts = [];
    private readonly List<List<History>> _histories = [];
    private readonly List<ScreenRenderer> _renderers = [];

    private int _screen;
    private int _fps;

    /// <summary>Carries out button presses. The headless daemon needs this as
    /// much as the tray app does - a capability that exists in only one of them
    /// is how swipe was lost when the tray app became the panel owner.</summary>
    private readonly ActionRunner _actions = new();
    private int _pressedButton = -1;
    private TimeSpan _flashUntil;
    /// <summary>Render-loop clock, so the press flash expires on wall time.</summary>
    private TimeSpan _clock;

    /// <summary>Opened ONLY when a screen actually carries a visualizer.
    /// A sensor-only configuration must not spawn a capture process, and
    /// most configurations are sensor-only.</summary>
    private NexusManager.Audio.AudioCapture? _audio;

    public Daemon(NexusDevice dev, SensorRegistry reg, ScreenSet set)
    {
        _dev = dev; _reg = reg; _set = set;

        _actions.GoToScreen = name =>
        {
            int i = _set.Screens.FindIndex(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            _screen = i;
            return true;
        };
        _actions.AdjustBrightness = delta =>
        {
            _set.Brightness = (int)Math.Clamp(_set.Brightness + delta, 0, 100);
            try { _dev.SetBrightness(_set.Brightness); } catch (Exception) { }
        };
        _screen = Math.Clamp(set.StartScreen, 0, Math.Max(0, set.Screens.Count - 1));

        foreach (var screen in set.Screens)
        {
            var (layout, buttons, visuals) = ScreenLayout.ComputeAll(screen, out var narrow);
            _buttonLayouts.Add(buttons);
            _visualLayouts.Add(visuals);
            foreach (string w in narrow) Console.Error.WriteLine($"  warning: {screen.Name}: {w}");
            _layouts.Add(layout);
            _renderers.Add(new ScreenRenderer(screen.Theme));
            // One sample per pixel column of the module it belongs to.
            _histories.Add(layout.Select(l => new History(Math.Max(2, (int)l.Rect.Width))).ToList());
        }

        // Every screen's sensors are sampled, not just the visible one, so a
        // screen you swipe back to shows continuous history rather than a gap.
        _reg.SetActive(set.Screens.SelectMany(s => s.Modules).Select(m => m.Source).Distinct());

        if (set.Screens.Any(s => s.NeedsAudio))
        {
            // Band count follows the widest visualizer on any screen, so a
            // 64-band cell is not fed a 32-band analysis.
            int bands = set.Screens.SelectMany(s => s.Visualizers)
                                   .Select(v => v.EffectiveBands).DefaultIfEmpty(32).Max();
            _audio = new NexusManager.Audio.AudioCapture(
                new NexusManager.Audio.AnalyserOptions { BandCount = bands });
            _audio.Start();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var canvas = new NexusCanvas();
        var frame = new byte[NexusDevice.FrameBytes];

        // Delta-based sources (CPU load, network throughput) cannot report
        // anything but NaN until they have two samples.
        _reg.Sample();
        await Task.Delay(250, ct).ConfigureAwait(false);
        _reg.Sample();

        _dev.SetBrightness(Math.Clamp(_set.Brightness, 0, 100));

        // ⛔ Paced on an ABSOLUTE deadline rather than tick * period, because the
        // period now varies: a screen carrying a visualizer runs at 30 while the
        // sensor screens stay at the configured rate. The old formula multiplied
        // one fixed period by the tick count, so a rate change mid-run would
        // have skewed every deadline after it.
        var nextFrame = TimeSpan.Zero;
        var sampleEvery = TimeSpan.FromMilliseconds(Math.Max(50, _set.SampleIntervalMs));
        var sw = Stopwatch.StartNew();
        int fails = 0;
        TimeSpan nextSample = TimeSpan.Zero;
        long tick = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var period = TimeSpan.FromSeconds(
                    1.0 / _set.Screens[_screen].EffectiveFps(_set.TargetFps));
                var wait = nextFrame - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                tick++;
                // Never let a slow frame push the deadline into the past and spin.
                nextFrame = (sw.Elapsed > nextFrame ? sw.Elapsed : nextFrame) + period;

                if (sw.Elapsed >= nextSample)
                {
                    nextSample = sw.Elapsed + sampleEvery;
                    _reg.Sample();
                    for (int s = 0; s < _layouts.Count; s++)
                        for (int i = 0; i < _layouts[s].Count; i++)
                        {
                            double v = _reg.Read(_layouts[s][i].Spec.Source);
                            if (!double.IsNaN(v)) _histories[s][i].Add(v);
                        }
                }

                int cur = _screen;
                _renderers[cur].Draw(canvas, _layouts[cur], _histories[cur],
                    k => { double v = _reg.Read(k); return double.IsNaN(v) ? 0 : v; },
                    _set.Screens[cur].Background, sw.Elapsed,
                    _buttonLayouts[cur], _pressedButton, _flashUntil,
                    _visualLayouts[cur], _audio?.Current,
                    (float)period.TotalSeconds);
                _clock = sw.Elapsed;

                if (_set.ShowPageIndicator)
                    PageIndicator.Draw(canvas.Canvas, cur, _set.Screens.Count,
                                       _set.Screens[cur].Theme.CaptionColor);

                canvas.CopyTo(frame);
                try
                {
                    _dev.PushFrame(frame);
                    fails = 0;
                }
                catch (Exception ex)
                {
                    // A service must survive the panel being unplugged. Without
                    // this the first failed write ends the render loop and the
                    // unit exits, so a replug needs a manual restart.
                    if (++fails >= 8)
                    {
                        Console.Error.WriteLine($"  panel lost ({ex.Message}); reconnecting");
                        fails = 0;
                        if (!await ReconnectAsync(ct).ConfigureAwait(false)) break;
                    }
                }
            }
        }
        finally
        {
            _fps = sw.Elapsed.TotalSeconds > 0 ? (int)(tick / sw.Elapsed.TotalSeconds) : 0;
            canvas.Dispose();
        }
    }

    /// <summary>
    /// Waits for the panel to come back, then reopens it.
    ///
    /// Returns false only when cancellation is requested, so the caller can
    /// tell "stopping" from "still waiting". The retry is slow on purpose:
    /// opening an absent HID device enumerates every node on the machine.
    /// </summary>
    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        try { _dev.Dispose(); } catch (Exception) { }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            try
            {
                var d = NexusDevice.Open();
                d.SetBrightness(Math.Clamp(_set.Brightness, 0, 100));
                _dev = d;
                Console.Error.WriteLine("  panel reconnected");
                return true;
            }
            catch (Exception) { /* still absent */ }
        }
        return false;
    }

    /// <summary>Swipe moves between screens; the gesture layer has already
    /// stitched the two-touch split a fast flick produces (D13).</summary>
    public void OnGesture(Gesture g)
    {
        if (_set.Screens.Count < 2) return;
        switch (g.Kind)
        {
            case GestureKind.SwipeLeft:  Advance(+1); break;   // content moves left => next
            case GestureKind.SwipeRight: Advance(-1); break;
            case GestureKind.Tap: OnTap(g); break;
            default: break;
        }
    }

    /// <summary>
    /// A tap. X is the only axis the panel reports, and buttons are full-height
    /// cells, so hit testing is a range check on the end position.
    /// </summary>
    private void OnTap(Gesture g)
    {
        var buttons = _buttonLayouts[_screen];
        int hit = ScreenLayout.HitTest(buttons, g.EndX);
        if (hit < 0) return;
        _pressedButton = hit;
        _flashUntil = _clock + ButtonRenderer.FlashDuration;
        Console.Error.WriteLine($"  button [{hit}] '{buttons[hit].Spec.Label}' -> {buttons[hit].Spec.Action}");
        _actions.Run(buttons[hit].Spec.Action);
    }

    private void Advance(int delta)
    {
        int n = _set.Screens.Count;
        int next = _screen + delta;
        _screen = _set.WrapScreens
            ? (next % n + n) % n
            : Math.Clamp(next, 0, n - 1);
    }

    public int AchievedFps => _fps;

    public void Dispose()
    {
        foreach (var r in _renderers) r.Dispose();
        _audio?.Dispose();
    }
}

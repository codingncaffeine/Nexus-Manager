using System.Diagnostics;
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
    private readonly NexusDevice _dev;
    private readonly SensorRegistry _reg;
    private readonly ScreenSet _set;

    private readonly List<List<ModuleLayout>> _layouts = [];
    private readonly List<List<History>> _histories = [];
    private readonly List<ScreenRenderer> _renderers = [];

    private int _screen;
    private int _fps;

    public Daemon(NexusDevice dev, SensorRegistry reg, ScreenSet set)
    {
        _dev = dev; _reg = reg; _set = set;
        _screen = Math.Clamp(set.StartScreen, 0, Math.Max(0, set.Screens.Count - 1));

        foreach (var screen in set.Screens)
        {
            var layout = ScreenLayout.Compute(screen, out var narrow);
            foreach (string w in narrow) Console.Error.WriteLine($"  warning: {screen.Name}: {w}");
            _layouts.Add(layout);
            _renderers.Add(new ScreenRenderer(screen.Theme));
            // One sample per pixel column of the module it belongs to.
            _histories.Add(layout.Select(l => new History(Math.Max(2, (int)l.Rect.Width))).ToList());
        }

        // Every screen's sensors are sampled, not just the visible one, so a
        // screen you swipe back to shows continuous history rather than a gap.
        _reg.SetActive(set.Screens.SelectMany(s => s.Modules).Select(m => m.Source).Distinct());
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

        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(_set.TargetFps, 1, 65));
        var sampleEvery = TimeSpan.FromMilliseconds(Math.Max(50, _set.SampleIntervalMs));
        var sw = Stopwatch.StartNew();
        TimeSpan nextSample = TimeSpan.Zero;
        long tick = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wait = TimeSpan.FromTicks(period.Ticks * tick) - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                tick++;

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
                    k => { double v = _reg.Read(k); return double.IsNaN(v) ? 0 : v; });

                if (_set.ShowPageIndicator)
                    PageIndicator.Draw(canvas.Canvas, cur, _set.Screens.Count,
                                       _set.Screens[cur].Theme.CaptionColor);

                canvas.CopyTo(frame);
                _dev.PushFrame(frame);
            }
        }
        finally
        {
            _fps = sw.Elapsed.TotalSeconds > 0 ? (int)(tick / sw.Elapsed.TotalSeconds) : 0;
            canvas.Dispose();
        }
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
            default: break;
        }
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
    }
}

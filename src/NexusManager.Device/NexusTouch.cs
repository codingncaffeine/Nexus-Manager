using System.Diagnostics;
using HidSharp;

namespace NexusManager.Device;

public enum GestureKind { Tap, SwipeLeft, SwipeRight }

public readonly record struct Gesture(
    GestureKind Kind,
    int StartX,
    int EndX,
    double DurationMs)
{
    public int Travel => EndX - StartX;
    /// <summary>Pixels per second. Meaningful for swipes; ~0 for taps.</summary>
    public double Velocity => DurationMs > 0 ? Travel / DurationMs * 1000.0 : 0;
}

/// <summary>
/// Turns raw NEXUS input reports into clean gestures.
///
/// Two hardware behaviours are handled here so nothing downstream has to know:
///
/// 1. A release report carrying X != 0 is NOT a release. It is a tracking
///    dropout during fast motion; the finger is still down and that X is a
///    valid position sample.
/// 2. A fast swipe drops one report and arrives as TWO down/up pairs about
///    10 ms apart. Ending a touch on the first release turns every fast swipe
///    into two taps — and it only reproduces when a human swipes quickly, so
///    careful manual testing never sees it (D13).
///
/// Both were measured; see _docs/PROTOCOL.md.
/// </summary>
public sealed class NexusTouch
{
    /// <summary>How long a release stays provisional before it counts as a real lift.</summary>
    public static readonly TimeSpan StitchWindow = TimeSpan.FromMilliseconds(30);

    /// <summary>Travel beyond this many pixels is a swipe rather than a tap.</summary>
    public int SwipeThresholdPx { get; init; } = 40;

    private readonly HidStream _stream;
    public NexusTouch(HidStream stream) => _stream = stream;

    public event Action<int>? Down;
    public event Action<int>? Move;
    public event Action<Gesture>? Gesture;

    public async Task RunAsync(CancellationToken ct)
    {
        var buf = new byte[512];
        _stream.ReadTimeout = 100;

        bool down = false;
        int startX = 0, lastX = 0;
        var clock = Stopwatch.StartNew();
        double downAt = 0;
        double releasedAt = double.NaN;      // set while a release is provisional

        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await _stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false); }
            catch (TimeoutException) { n = 0; }
            catch (OperationCanceledException) { break; }

            double now = clock.Elapsed.TotalMilliseconds;

            if (n >= 8)
            {
                bool pressed = buf[5] != 0;
                int x = buf[6] | (buf[7] << 8);

                // Behaviour 1: a "release" with a real coordinate is a dropout,
                // not a lift. Treat it as continued contact at that position.
                if (!pressed && x != 0)
                {
                    pressed = true;
                }

                if (pressed)
                {
                    if (!down)
                    {
                        if (!double.IsNaN(releasedAt))
                        {
                            // Behaviour 2: re-press inside the stitch window is the
                            // same physical swipe resuming. Keep the original start.
                            releasedAt = double.NaN;
                        }
                        else
                        {
                            down = true; startX = x; downAt = now;
                            Down?.Invoke(x);
                        }
                    }
                    if (x != lastX) Move?.Invoke(x);
                    lastX = x;
                }
                else if (down)
                {
                    // Provisional. Do not emit anything yet.
                    releasedAt = now;
                }
            }

            // Finalise a release only once the stitch window has passed with no re-press.
            if (down && !double.IsNaN(releasedAt) &&
                now - releasedAt >= StitchWindow.TotalMilliseconds)
            {
                down = false;
                releasedAt = double.NaN;

                int travel = lastX - startX;
                var kind = Math.Abs(travel) < SwipeThresholdPx ? GestureKind.Tap
                         : travel > 0 ? GestureKind.SwipeRight
                         : GestureKind.SwipeLeft;

                Gesture?.Invoke(new Gesture(kind, startX, lastX, now - downAt));
            }
        }
    }
}

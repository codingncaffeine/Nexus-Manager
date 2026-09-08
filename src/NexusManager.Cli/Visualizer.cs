using System.Globalization;
using NexusManager.Audio;
using NexusManager.Device;
using NexusManager.Render;

namespace NexusManager.Cli;

/// <summary>
/// Spike instruments for the music visualizer (E0-E4).
///
/// Three entry points, in the order they must be trusted:
/// E1 <see cref="SelfTest"/>  — the DSP is right, proved on synthetic signal.
/// E0 <see cref="Probe"/>     — capture reaches real audio, and says which
///                              failure it is when it does not.
/// E3 <see cref="RunAsync"/>  — it draws on the panel, with a latency report.
/// </summary>
public static class Visualizer
{
    // ------------------------------------------------------------------ E1 --

    /// <summary>
    /// Validates the analysis chain against signals whose answer is known
    /// beforehand. No hardware, no sound, no audio server.
    ///
    /// ⛔ This exists because a wrong band mapping does not announce itself. It
    /// shows up as "the display looks a bit odd" after the renderer, the panel
    /// and the capture are all also new and also suspects.
    /// </summary>
    public static int SelfTest()
    {
        var o = new AnalyserOptions();
        int fails = 0;
        Console.WriteLine($"  DSP self-test: {o.FftSize}-point, {o.BandCount} bands, "
                        + $"{o.MinHz:F0}-{o.MaxHz:F0} Hz at {o.SampleRate} Hz");
        // The falling peak cap. Three separate claims, each checked separately.
        //
        // ⛔ The first version of this test asserted the cap ends at zero, and it
        // FAILED against correct code. The bar does not vanish when the signal
        // stops - it decays at 15 dB/s - so after ~1.3 s it is still at 0.69 and
        // the cap is correctly sitting ON it. The cap's floor is the BAR, not the
        // bottom of the display, and once landed it rides the bar down at the
        // bar's constant rate, which also ends the acceleration. Both "failures"
        // were the instrument describing a display it had not thought through.
        {
            var a = new SpectrumAnalyser(o);
            var loud = new float[o.FftSize];
            var quiet = new float[o.FftSize];
            for (int i = 0; i < loud.Length; i++)
                loud[i] = MathF.Sin(2f * MathF.PI * 1000f * i / o.SampleRate);

            float dt = (float)o.HopSize / o.SampleRate;
            a.Process(loud, dt);
            int band = ArgMax(a.Publish(quiet, 0, 0, 0, 0, 0f, TimeSpan.Zero).Bands);

            var bars = new List<float>();
            var caps = new List<float>();
            for (int h = 0; h < 240; h++)          // ~5 s: long enough for the bar to reach the floor
            {
                a.Process(quiet, dt);
                var f = a.Publish(quiet, 0, 0, 0, 0, -120f, TimeSpan.Zero);
                bars.Add(f.Bands[band]);
                caps.Add(f.Peaks[band]);
            }

            // 1. It HOLDS, detached, while the bar drops away beneath it.
            int hold = 0;
            while (hold < caps.Count && caps[hold] >= caps[0] - 0.001f) hold++;
            float holdSeconds = hold * dt;
            bool okHold = holdSeconds >= 0.25f && holdSeconds <= 0.75f;

            // 2. It ACCELERATES while in free fall - strictly above the bar.
            //    Each step must be larger than the one before it. That is the
            //    whole difference between a falling object and a decaying value.
            int accel = 0, steps = 0;
            for (int i = hold + 1; i < caps.Count - 1; i++)
            {
                // BOTH endpoints must be in free fall. The step that ends in
                // the landing is clamped to the bar, so its delta is short
                // through no fault of the physics - measuring it reported
                // 14/15 against a cap that was accelerating perfectly.
                if (caps[i] <= bars[i] + 1e-4f) break;
                if (caps[i + 1] <= bars[i + 1] + 1e-4f) break;
                float d1 = caps[i - 1] - caps[i], d2 = caps[i] - caps[i + 1];
                steps++;
                if (d2 >= d1 - 1e-4f) accel++;
            }
            bool okAccel = steps >= 5 && accel == steps;

            // 3. It LANDS ON THE BAR and stays there - not through it, not above it.
            bool okLand = MathF.Abs(caps[^1] - bars[^1]) < 0.01f;

            bool ok = okHold && okAccel && okLand;
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  peak cap -> holds {holdSeconds:F2}s"
                            + $" (want 0.25-0.75), {accel}/{steps} free-fall steps accelerating,"
                            + $" settles on bar (cap {caps[^1]:F3} vs bar {bars[^1]:F3})");
        }

        Console.WriteLine();

        // Ratio between adjacent band centres. A tone must land in a band whose
        // centre is within half this in log space, or the mapping is wrong.
        double ratio = Math.Pow(o.MaxHz / o.MinHz, 1.0 / o.BandCount);

        foreach (float hz in new[] { 100f, 440f, 1000f, 5000f, 10000f })
        {
            var (a, frame) = Analyse(o, n => MathF.Sin(2f * MathF.PI * hz * n / o.SampleRate));

            int peak = ArgMax(frame.Bands);
            float centre = a.BandCentres[peak];
            double err = Math.Abs(Math.Log(centre / hz) / Math.Log(ratio));

            // Energy must also be CONCENTRATED. A mapping that smears a tone
            // across the strip can still put its maximum in the right place.
            float spill = 0f;
            for (int b = 0; b < frame.Bands.Length; b++)
                if (Math.Abs(b - peak) > 2) spill = MathF.Max(spill, frame.Bands[b]);

            bool ok = err <= 0.5 && frame.Bands[peak] >= 0.75f && spill < 0.35f;
            if (!ok) fails++;
            Console.WriteLine(
                $"  {(ok ? "OK  " : "FAIL")}  {hz,6:F0} Hz -> band {peak,3} "
              + $"(centre {centre,8:F1} Hz, {err:F2} bands off)  "
              + $"level {frame.Bands[peak]:F3}  max spill {spill:F3}");
        }

        // Silence: every band on the floor, and the gate closed.
        {
            var (_, frame) = Analyse(o, _ => 0f);
            float max = 0f;
            foreach (float v in frame.Bands) max = MathF.Max(max, v);
            bool ok = max < 0.01f && frame.Silent;
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  silence -> max band {max:F4}, "
                            + $"gate {(frame.Silent ? "closed" : "OPEN")}");
        }

        // ⛔ POSITIVE CONTROL for the gate. A silence gate that also swallows
        // real signal would make every check above pass while the panel stayed
        // dark. A gate that fires on correct work is deleted, not silenced.
        {
            var (_, frame) = Analyse(o, n => 0.5f * MathF.Sin(2f * MathF.PI * 1000f * n / o.SampleRate));
            bool ok = !frame.Silent;
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  -6 dBFS tone -> gate "
                            + $"{(frame.Silent ? "CLOSED (wrong)" : "open")}, peak {frame.PeakDbfs:F1} dBFS");
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "  DSP self-test PASSED" : $"  DSP self-test FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    private static (SpectrumAnalyser, AudioFrame) Analyse(AnalyserOptions o, Func<int, float> signal)
    {
        var a = new SpectrumAnalyser(o);
        var buf = new float[o.FftSize];
        for (int i = 0; i < buf.Length; i++) buf[i] = signal(i);

        // One hop is enough: attack is instant by design (D48), so a single
        // pass from the floor lands at the true level.
        a.Process(buf, (float)o.HopSize / o.SampleRate);

        float peak = 0f;
        foreach (float v in buf) peak = MathF.Max(peak, MathF.Abs(v));
        var frame = a.Publish(buf.AsSpan(0, Math.Min(o.WaveformLength, buf.Length)),
                              0f, 0f, 0f, 0f, SpectrumAnalyser.ToDb(peak), TimeSpan.Zero);
        return (a, frame);
    }

    private static int ArgMax(float[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    // ------------------------------------------------------------------ E0 --

    /// <summary>
    /// Says what the capture is actually doing, and WHICH kind of nothing it is
    /// seeing when it sees nothing (D51).
    ///
    /// ⛔ Run it twice: once with audio playing and once without. A probe that
    /// prints plausible numbers during silence is reading zeros and lying. The
    /// silent run is the positive control, not an afterthought.
    /// </summary>
    public static async Task<int> ProbeAsync(int seconds)
    {
        string? sink = AudioCapture.DefaultSink();
        Console.WriteLine($"  default sink : {sink ?? "(none)"}");
        if (sink is null)
        {
            Console.WriteLine("  no default sink - is PipeWire/PulseAudio running?");
            return 1;
        }
        Console.WriteLine($"  monitor      : {sink}.monitor "
                        + $"({(AudioCapture.MonitorRunning(sink) ? "RUNNING" : "SUSPENDED")})");
        Console.WriteLine();

        var o = new AnalyserOptions();
        using var cap = new AudioCapture(o);
        cap.Start();

        Console.WriteLine("  t     peak dBFS   gate    hops   samples   bands (32, low to high)");
        float worst = -140f, best = -140f;
        for (int t = 0; t < seconds; t++)
        {
            await Task.Delay(1000);
            var f = cap.Current;
            var s = cap.Status;
            best = MathF.Max(best, f.PeakDbfs);
            if (worst <= -140f || f.PeakDbfs < worst) worst = f.PeakDbfs;

            Console.WriteLine($"  {t,-4}  {f.PeakDbfs,8:F1}   {(f.Silent ? "shut" : "open"),-5} "
                            + $"{s.Hops,7} {s.SamplesRead,9}   {Spark(f.Bands)}");
        }

        var final = cap.Status;
        Console.WriteLine();
        Console.WriteLine($"  peak range   : {worst:F1} to {best:F1} dBFS");
        Console.WriteLine($"  hops         : {final.Hops} ({final.Hops / (double)seconds:F1}/s, expected "
                        + $"{(double)o.SampleRate / o.HopSize:F1}/s)");
        Console.WriteLine($"  restarts     : {final.Restarts}");
        if (final.Error is not null) Console.WriteLine($"  error        : {final.Error}");

        if (final.Hops == 0)
        {
            Console.WriteLine("  VERDICT: no analysis hops at all - capture never delivered samples.");
            return 1;
        }
        if (best <= -139f)
        {
            Console.WriteLine("  VERDICT: samples flowed but every one was ZERO. Wrong source, or a");
            Console.WriteLine("           suspended monitor. This is the pw-record failure in D44.");
            return 1;
        }
        Console.WriteLine("  VERDICT: capture is delivering real signal.");
        return 0;
    }

    private const string Blocks = " ▁▂▃▄▅▆▇█";

    private static string Spark(float[] bands)
    {
        var sb = new System.Text.StringBuilder(bands.Length);
        foreach (float v in bands)
            sb.Append(Blocks[Math.Clamp((int)(v * (Blocks.Length - 1)), 0, Blocks.Length - 1)]);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ E3 --

    /// <summary>
    /// Draws the bars mode on the real panel, and reports the latency it can
    /// actually measure.
    ///
    /// ⛔ The reported figure is OUR pipeline only: capture read to panel write.
    /// The PipeWire graph leg (application to sink to monitor) is not ours to
    /// time and is NOT included. Presenting the sum as one measured number would
    /// be a prediction wearing a measurement's clothes.
    /// </summary>
    public static async Task<int> RunAsync(
        int seconds, int fps, int bands, VisualizerKind? mode, int cycleSeconds, CancellationToken ct)
    {
        var o = new AnalyserOptions { BandCount = bands };
        // Cycling is the point of the spike run: seventeen modes are a
        // catalogue until they have been seen one after another on the panel.
        var modes = mode is { } m ? new[] { m } : VisualizerRenderer.Implemented;
        var spec = new VisualizerSpec { BandCount = bands, Kind = modes[0] };
        int modeIndex = 0;
        var nextSwitch = TimeSpan.FromSeconds(cycleSeconds);
        var theme = new Theme();

        using var cap = new AudioCapture(o);
        cap.Start();

        using var dev = NexusDevice.Open();
        using var canvas = new NexusCanvas();
        using var vis = new VisualizerRenderer();
        var frameBytes = new byte[NexusDevice.FrameBytes];
        var rect = new SkiaSharp.SKRect(0, 0, NexusCanvas.Width, NexusCanvas.Height);

        dev.SetBrightness(100);

        var latencies = new List<double>();
        long stale = 0, drawn = 0, lastSeq = -1;
        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 65));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stop = TimeSpan.FromSeconds(seconds);
        long tick = 0;

        Console.WriteLine($"  {bands} bands at {fps} fps for {seconds}s. Ctrl-C to stop early.");
        Console.WriteLine(modes.Length > 1
            ? $"  cycling {modes.Length} modes every {cycleSeconds}s"
            : $"  mode: {modes[0]}");
        Console.WriteLine($"  [  0.0s] {spec.Kind}");

        try
        {
            while (!ct.IsCancellationRequested && sw.Elapsed < stop)
            {
                var wait = TimeSpan.FromTicks(period.Ticks * tick) - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                tick++;

                if (modes.Length > 1 && sw.Elapsed >= nextSwitch)
                {
                    nextSwitch = sw.Elapsed + TimeSpan.FromSeconds(cycleSeconds);
                    modeIndex = (modeIndex + 1) % modes.Length;
                    spec.Kind = modes[modeIndex];
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,5:F1}s] {spec.Kind}");
                }

                var frame = cap.Current;
                canvas.Clear(theme.BackgroundColor);
                vis.Draw(canvas.Canvas, spec, rect, frame, theme);
                canvas.CopyTo(frameBytes);
                dev.PushFrame(frameBytes);
                drawn++;

                // A repeated sequence number means the render loop outran the
                // analyser and redrew the same audio. Counted, not hidden: it is
                // the difference between a smooth display and a truthful one.
                if (frame.Sequence == lastSeq) stale++;
                else if (frame.Sequence > 0)
                {
                    lastSeq = frame.Sequence;
                    latencies.Add((AudioClock.Elapsed - frame.Timestamp).TotalMilliseconds);
                }
            }
        }
        finally
        {
            dev.HandBack(1, 100);
        }

        Console.WriteLine();
        Console.WriteLine($"  frames drawn : {drawn} ({drawn / sw.Elapsed.TotalSeconds:F1} fps achieved)");
        Console.WriteLine($"  repeated     : {stale} ({(drawn > 0 ? 100.0 * stale / drawn : 0):F1}% redrew the same hop)");
        Console.WriteLine($"  capture      : {cap.Status.Hops} hops, {cap.Status.Restarts} restarts");

        if (latencies.Count > 0)
        {
            latencies.Sort();
            Console.WriteLine();
            Console.WriteLine("  MEASURED pipeline latency (capture read -> panel write):");
            Console.WriteLine($"    min {latencies[0]:F1} ms   "
                            + $"p50 {Pct(latencies, 0.50):F1} ms   "
                            + $"p95 {Pct(latencies, 0.95):F1} ms   "
                            + $"max {latencies[^1]:F1} ms");
            Console.WriteLine("  ⛔ EXCLUDES the PipeWire graph leg (app -> sink -> monitor),");
            Console.WriteLine("     which is not ours to time. The total a listener perceives is");
            Console.WriteLine("     this PLUS that, and this report does not know that number.");
        }
        return 0;
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    /// <summary>Resolves --mode. Returns null to cycle every mode.</summary>
    public static VisualizerKind? ParseMode(string[] args)
    {
        int i = Array.IndexOf(args, "--mode");
        if (i < 0 || i + 1 >= args.Length) return null;
        string want = args[i + 1];
        // Implemented only: accepting a named-but-unbuilt mode would draw Bars
        // while reporting the name the user asked for.
        foreach (var k in VisualizerRenderer.Implemented)
            if (string.Equals(k.ToString(), want, StringComparison.OrdinalIgnoreCase))
                return k;
        Console.WriteLine($"  unknown mode {want}; known: "
            + string.Join(", ", VisualizerRenderer.Implemented));
        return null;
    }

    public static int ParseInt(string[] args, string flag, int fallback)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length
            && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;
    }
}

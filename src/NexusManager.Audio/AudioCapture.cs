using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusManager.Audio;

/// <summary>Shared monotonic clock, so a capture timestamp and a render
/// timestamp are comparable and end-to-end latency is measurable rather than
/// estimated.</summary>
public static class AudioClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static TimeSpan Elapsed => Sw.Elapsed;
}

/// <summary>What the capture thread is currently doing. Drives the probe (D51).</summary>
public sealed record CaptureStatus(
    string Sink,
    bool Running,
    long SamplesRead,
    long Hops,
    long Restarts,
    string? Error);

/// <summary>
/// Captures the default sink's monitor and runs the analysis chain on it.
///
/// Uses `parec` over the PulseAudio interface, per D44 — chosen by measurement,
/// not preference: `pw-record` targeting a `.monitor` name captured pure zeros
/// and did not error, because a PipeWire monitor is a PORT on the sink node
/// rather than a node of its own.
///
/// ⛔ The render loop must NEVER block on this class. <see cref="Current"/> is a
/// plain reference read of an immutable frame; if capture stalls, the last frame
/// simply stops advancing and <see cref="AudioFrame.Sequence"/> stops moving,
/// which is how a caller tells a stall from a static signal.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly AnalyserOptions _o;
    private readonly SpectrumAnalyser _analyser;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    /// <summary>Analysis history. Ring, not a sliding window - see RingBuffer.</summary>
    private readonly RingBuffer _ring;
    private readonly RingBuffer _waveRing;
    /// <summary>Per-channel history, for the goniometer. Kept separately
    /// because a vectorscope cannot be derived from the mono mix - the mix
    /// is exactly the information it exists to show.</summary>
    private readonly RingBuffer _waveRingL;
    private readonly RingBuffer _waveRingR;
    /// <summary>Copy-out scratch, reused so a hop allocates nothing.</summary>
    private readonly float[] _window;
    private readonly float[] _waveform;
    private readonly float[] _waveformL;
    private readonly float[] _waveformR;

    private volatile AudioFrame _current;
    private volatile string _sink = "(unresolved)";
    private volatile bool _running;
    private volatile string? _error;
    private long _samplesRead;
    private long _hops;
    private long _restarts;

    public AudioFrame Current => _current;

    public CaptureStatus Status => new(
        _sink, _running,
        Interlocked.Read(ref _samplesRead),
        Interlocked.Read(ref _hops),
        Interlocked.Read(ref _restarts),
        _error);

    public AudioCapture(AnalyserOptions options)
    {
        _o = options;
        _analyser = new SpectrumAnalyser(options);
        _ring = new RingBuffer(_o.FftSize);
        _waveRing = new RingBuffer(_o.WaveformLength);
        _waveRingL = new RingBuffer(_o.WaveformLength);
        _waveRingR = new RingBuffer(_o.WaveformLength);
        _window = new float[_o.FftSize];
        _waveform = new float[_o.WaveformLength];
        _waveformL = new float[_o.WaveformLength];
        _waveformR = new float[_o.WaveformLength];
        _current = AudioFrame.Empty(_o.BandCount, _o.WaveformLength);
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "nexus-audio",
            // Above normal: a late audio hop shows as a visibly stuttering
            // display, and the work per hop is tiny.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Resolves the default sink. ⛔ D43: this machine has four sinks and the
    /// default moves. Hardcoding one produces a display that is silently, and
    /// convincingly, flat.
    /// </summary>
    public static string? DefaultSink()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pactl", "get-default-sink")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            string name = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether a sink's monitor is actually RUNNING. A SUSPENDED
    /// monitor emits zeros rather than an error, so "flat" needs this to be
    /// distinguishable from silence (D51).</summary>
    public static bool MonitorRunning(string sink)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pactl", "list short sources")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            foreach (string line in all.Split('\n'))
                if (line.Contains(sink + ".monitor", StringComparison.Ordinal))
                    return line.Contains("RUNNING", StringComparison.Ordinal);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            string? sink = DefaultSink();
            if (sink is null)
            {
                _error = "no default sink (is PipeWire/PulseAudio running?)";
                _running = false;
                if (_cts.Token.WaitHandle.WaitOne(2000)) return;
                continue;
            }

            _sink = sink;
            try
            {
                CaptureOne(sink);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }

            _running = false;
            if (_cts.IsCancellationRequested) return;
            Interlocked.Increment(ref _restarts);
            // parec exited, or the sink changed under us. Either way, resolve
            // the default again rather than reopening the old one.
            if (_cts.Token.WaitHandle.WaitOne(500)) return;
        }
    }

    private void CaptureOne(string sink)
    {
        var psi = new ProcessStartInfo("parec")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(sink + ".monitor");
        psi.ArgumentList.Add("--rate=" + _o.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--channels=2");
        psi.ArgumentList.Add("--format=float32le");
        psi.ArgumentList.Add("--latency-msec=20");
        psi.ArgumentList.Add("--raw");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start parec");
        try
        {
            _error = null;
            _running = true;
            Pump(proc, sink);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    private void Pump(Process proc, string sink)
    {
        // 2 channels x float32. One read is well under the hop so the newest
        // sample is never stale by more than the pipe's own buffering.
        var bytes = new byte[4096 * 2 * 4];
        var stream = proc.StandardOutput.BaseStream;

        int sinceHop = 0;
        float rmsAccL = 0f, rmsAccR = 0f, peakL = 0f, peakR = 0f, monoPeak = 0f;
        int accCount = 0;
        var lastCheck = AudioClock.Elapsed;

        while (!_cts.IsCancellationRequested)
        {
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read <= 0) return;                       // parec exited
            var stamp = AudioClock.Elapsed;

            var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read - (read % 8)));
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                float l = samples[i], r = samples[i + 1];
                float mono = 0.5f * (l + r);

                _ring.Add(mono);
                _waveRing.Add(mono);
                _waveRingL.Add(l);
                _waveRingR.Add(r);

                float al = MathF.Abs(l), ar = MathF.Abs(r), am = MathF.Abs(mono);
                rmsAccL += l * l; rmsAccR += r * r;
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
                if (am > monoPeak) monoPeak = am;
                accCount++;

                if (++sinceHop < _o.HopSize) continue;
                sinceHop = 0;
                // ⛔ A partly-filled ring is zeros followed by signal, which
                // reads to the FFT as a hard edge and paints a bogus
                // full-spectrum flash on the first frame after every restart.
                if (!_ring.Full)
                {
                    // Warm-up still counts as a hop boundary, so the
                    // accumulators reset with it. Leaving them running
                    // would fold the leading silence into the first
                    // real frames RMS.
                    rmsAccL = rmsAccR = peakL = peakR = monoPeak = 0f;
                    accCount = 0;
                    continue;
                }

                _ring.CopyOrdered(_window);
                _waveRing.CopyOrdered(_waveform);
                _waveRingL.CopyOrdered(_waveformL);
                _waveRingR.CopyOrdered(_waveformR);

                float dt = (float)_o.HopSize / _o.SampleRate;
                _analyser.Process(_window, dt);

                _current = _analyser.Publish(
                    _waveform,
                    MathF.Sqrt(rmsAccL / accCount), MathF.Sqrt(rmsAccR / accCount),
                    peakL, peakR,
                    SpectrumAnalyser.ToDb(monoPeak),
                    // ⛔ The timestamp is when the NEWEST sample in this window
                    // was read, not when the hop finished. That is what makes
                    // "now minus this" a real latency rather than a render-time
                    // measurement of our own arithmetic.
                    stamp,
                    _waveformL, _waveformR);

                Interlocked.Increment(ref _hops);
                rmsAccL = rmsAccR = peakL = peakR = monoPeak = 0f;
                accCount = 0;
            }

            Interlocked.Add(ref _samplesRead, samples.Length / 2);

            // Default sink moved? Drop this capture and let Loop reopen (D43).
            if (AudioClock.Elapsed - lastCheck > TimeSpan.FromSeconds(3))
            {
                lastCheck = AudioClock.Elapsed;
                string? now = DefaultSink();
                if (now is not null && !string.Equals(now, sink, StringComparison.Ordinal)) return;
            }
        }
    }


    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(1500);
        _cts.Dispose();
    }
}

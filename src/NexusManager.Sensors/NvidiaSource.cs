using System.Diagnostics;
using System.Globalization;

namespace NexusManager.Sensors;

/// <summary>
/// NVIDIA GPUs via nvidia-smi. amdgpu already appears through the hwmon sweep,
/// but the proprietary NVIDIA driver exposes almost nothing there, so this is
/// the only way to reach temperature, utilisation, VRAM, power and clocks.
///
/// Spawning a process costs 10-20 ms, far too much per frame, so sampling runs
/// on its own slow cadence and <see cref="Sample"/> only publishes the latest
/// cached values.
/// </summary>
public sealed class NvidiaSource : ISensorSource, IDisposable
{
    private static readonly string[] Fields =
    [
        "temperature.gpu", "utilization.gpu", "utilization.memory",
        "memory.used", "memory.total", "power.draw", "clocks.gr", "fan.speed",
    ];

    private readonly Dictionary<string, double> _latest = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private readonly List<string> _gpuNames = [];

    public IEnumerable<SensorDescriptor> Discover()
    {
        _gpuNames.Clear();
        string? csv = RunSmi("--query-gpu=name --format=csv,noheader");
        if (csv is null) return [];

        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            _gpuNames.Add(line.Trim());
        if (_gpuNames.Count == 0) return [];

        var list = new List<SensorDescriptor>();
        for (int i = 0; i < _gpuNames.Count; i++)
        {
            string n = _gpuNames[i];
            list.Add(new SensorDescriptor($"gpu.{i}.temp",      "Temperature",  n, SensorKind.Temperature, Unit.Celsius,   20, 95, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.load",      "GPU Load",     n, SensorKind.Load,        Unit.Percent,    0, 100, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.memload",   "Memory Load",  n, SensorKind.Load,        Unit.Percent,    0, 100, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.mem.used",  "VRAM Used",    n, SensorKind.Memory,      Unit.Bytes,      0, 32, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.power",     "Power Draw",   n, SensorKind.Power,       Unit.Watt,       0, 450, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.clock",     "Core Clock",   n, SensorKind.Frequency,   Unit.Megahertz,  0, 3200, DeviceCategory.Gpu));
            list.Add(new SensorDescriptor($"gpu.{i}.fan",       "Fan Speed",    n, SensorKind.Fan,         Unit.Percent,    0, 100, DeviceCategory.Gpu));
        }
        return list;
    }

    /// <summary>Begins background polling. Without this, values stay at zero.</summary>
    public void Start(TimeSpan interval)
    {
        if (_gpuNames.Count == 0) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoop(interval, _cts.Token));
    }

    private void PollLoop(TimeSpan interval, CancellationToken ct)
    {
        string query = "--query-gpu=" + string.Join(",", Fields) + " --format=csv,noheader,nounits";
        while (!ct.IsCancellationRequested)
        {
            string? csv = RunSmi(query);
            if (csv is not null)
            {
                var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                lock (_gate)
                {
                    for (int g = 0; g < rows.Length && g < _gpuNames.Count; g++)
                    {
                        var v = rows[g].Split(',', StringSplitOptions.TrimEntries);
                        if (v.Length < Fields.Length) continue;
                        Put($"gpu.{g}.temp",     v[0]);
                        Put($"gpu.{g}.load",     v[1]);
                        Put($"gpu.{g}.memload",  v[2]);
                        Put($"gpu.{g}.mem.used", v[3], 1.0 / 1024.0);   // MiB -> GiB
                        Put($"gpu.{g}.power",    v[5]);
                        Put($"gpu.{g}.clock",    v[6]);
                        Put($"gpu.{g}.fan",      v[7]);
                    }
                }
            }
            try { Task.Delay(interval, ct).Wait(ct); }
            catch (OperationCanceledException) { break; }
            catch (AggregateException) { break; }
        }

        void Put(string key, string text, double scale = 1.0)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                _latest[key] = d * scale;
        }
    }

    public void Sample(IDictionary<string, double> into)
    {
        lock (_gate)
            foreach (var kv in _latest) into[kv.Key] = kv.Value;
    }

    private static string? RunSmi(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) return null;
            return p.ExitCode == 0 ? outp : null;
        }
        catch (Exception) { return null; }   // no NVIDIA driver, or nvidia-smi absent
    }

    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
}

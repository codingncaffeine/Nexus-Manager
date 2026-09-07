using System.Globalization;

namespace NexusManager.Sensors;

/// <summary>Overall and per-core CPU utilisation from /proc/stat.</summary>
public sealed class CpuLoadSource : ISensorSource
{
    private readonly Dictionary<string, (long Idle, long Total)> _prev = [];
    private int _cores;

    public IEnumerable<SensorDescriptor> Discover()
    {
        string cpu = DeviceCatalog.CpuModel() ?? "Processor";
        var list = new List<SensorDescriptor>
        {
            new("cpu.load", "Total Load", cpu, SensorKind.Load, Unit.Percent, 0, 100, DeviceCategory.Processor),
        };
        _cores = 0;
        try
        {
            foreach (string line in File.ReadLines("/proc/stat"))
                if (line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsDigit(line[3]))
                    _cores++;
        }
        catch (IOException) { }

        for (int i = 0; i < _cores; i++)
            list.Add(new SensorDescriptor($"cpu.core{i}.load", $"Core {i}", cpu,
                                          SensorKind.Load, Unit.Percent, 0, 100, DeviceCategory.Processor));
        return list;
    }

    public void Sample(IDictionary<string, double> into)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 6) continue;

                string key = f[0] == "cpu" ? "cpu.load" : $"cpu.core{f[0][3..]}.load";
                long total = 0, idle = 0;
                for (int i = 1; i < f.Length; i++)
                {
                    if (!long.TryParse(f[i], out long v)) continue;
                    total += v;
                    if (i is 4 or 5) idle += v;          // idle + iowait
                }

                if (_prev.TryGetValue(key, out var p))
                {
                    long dT = total - p.Total, dI = idle - p.Idle;
                    if (dT > 0) into[key] = 100.0 * (dT - dI) / dT;
                }
                _prev[key] = (idle, total);
            }
        }
        catch (IOException) { }
    }
}

/// <summary>RAM and swap from /proc/meminfo.</summary>
public sealed class MemorySource : ISensorSource
{
    public IEnumerable<SensorDescriptor> Discover() =>
    [
        new("mem.used.percent", "Memory Used", "System Memory", SensorKind.Memory, Unit.Percent, 0, 100, DeviceCategory.Dram),
        new("mem.used.gb",      "Memory Used", "System Memory", SensorKind.Memory, Unit.Bytes,   0, 128, DeviceCategory.Dram),
        new("mem.available.gb", "Memory Free", "System Memory", SensorKind.Memory, Unit.Bytes,   0, 128, DeviceCategory.Dram),
        new("swap.used.percent","Swap Used",   "System Memory", SensorKind.Memory, Unit.Percent, 0, 100, DeviceCategory.Dram),
    ];

    public void Sample(IDictionary<string, double> into)
    {
        double total = 0, avail = 0, swapTotal = 0, swapFree = 0;
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                var f = line.Split(':', 2);
                if (f.Length != 2) continue;
                var v = f[1].Trim().Split(' ')[0];
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb)) continue;
                switch (f[0])
                {
                    case "MemTotal": total = kb; break;
                    case "MemAvailable": avail = kb; break;
                    case "SwapTotal": swapTotal = kb; break;
                    case "SwapFree": swapFree = kb; break;
                }
            }
        }
        catch (IOException) { return; }

        if (total > 0)
        {
            into["mem.used.percent"] = 100.0 * (total - avail) / total;
            into["mem.used.gb"] = (total - avail) / 1048576.0;
            into["mem.available.gb"] = avail / 1048576.0;
        }
        if (swapTotal > 0)
            into["swap.used.percent"] = 100.0 * (swapTotal - swapFree) / swapTotal;
    }
}

/// <summary>Throughput per network interface, from sysfs byte counters.</summary>
public sealed class NetworkSource : ISensorSource
{
    private const string Root = "/sys/class/net";
    private readonly Dictionary<string, (long Rx, long Tx, long Ticks)> _prev = [];
    private List<string> _ifaces = [];

    public IEnumerable<SensorDescriptor> Discover()
    {
        var list = new List<SensorDescriptor>();
        _ifaces = [];
        if (!Directory.Exists(Root)) return list;

        foreach (string dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (name == "lo") continue;
            if (!File.Exists(Path.Combine(dir, "statistics", "rx_bytes"))) continue;
            _ifaces.Add(name);
            string nice = NicName(name);
            list.Add(new SensorDescriptor($"net.{name}.rx", "Download", nice, SensorKind.Network, Unit.BytesPerSec, 0, 125, DeviceCategory.Network));
            list.Add(new SensorDescriptor($"net.{name}.tx", "Upload",   nice, SensorKind.Network, Unit.BytesPerSec, 0, 125, DeviceCategory.Network));
        }
        return list;
    }

    public void Sample(IDictionary<string, double> into)
    {
        long now = Environment.TickCount64;
        foreach (string n in _ifaces)
        {
            if (!TryReadLong($"{Root}/{n}/statistics/rx_bytes", out long rx)) continue;
            if (!TryReadLong($"{Root}/{n}/statistics/tx_bytes", out long tx)) continue;

            if (_prev.TryGetValue(n, out var p))
            {
                double secs = (now - p.Ticks) / 1000.0;
                if (secs > 0.05)
                {
                    into[$"net.{n}.rx"] = Math.Max(0, rx - p.Rx) / secs / 1048576.0;
                    into[$"net.{n}.tx"] = Math.Max(0, tx - p.Tx) / secs / 1048576.0;
                }
            }
            _prev[n] = (rx, tx, now);
        }
    }


    /// <summary>"eno1" means nothing to a person; interface prefixes are
    /// predictable enough to name the adapter type.</summary>
    private static string NicName(string iface)
    {
        string kind = iface switch
        {
            _ when iface.StartsWith("en", StringComparison.Ordinal) => "Ethernet",
            _ when iface.StartsWith("wl", StringComparison.Ordinal) => "Wi-Fi",
            _ when iface.StartsWith("ww", StringComparison.Ordinal) => "Mobile Broadband",
            _ when iface.StartsWith("br", StringComparison.Ordinal) => "Bridge",
            _ when iface.StartsWith("tun", StringComparison.Ordinal)
                || iface.StartsWith("wg", StringComparison.Ordinal)  => "VPN",
            _ => "Network",
        };
        return $"{kind} ({iface})";
    }
    private static bool TryReadLong(string path, out long v)
    {
        v = 0;
        try { return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out v); }
        catch (IOException) { return false; }
    }
}

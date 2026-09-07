using System.Globalization;

namespace NexusManager.Sensors;

/// <summary>
/// Sweeps /sys/class/hwmon for every sensor the kernel exposes: temperatures,
/// fans, voltages, power, current and frequency. This is where the breadth comes
/// from — on a typical desktop it finds NVMe drives, RAM modules, chipset, VRM,
/// wifi and GPU sensors that no hand-written list would have thought to include.
///
/// Everything is keyed by DRIVER NAME, never by hwmonN, because those indices
/// shift with module load order between boots.
/// </summary>
public sealed class HwmonSource : ISensorSource
{
    private const string Root = "/sys/class/hwmon";

    private readonly record struct Entry(string Key, string Path, Unit Unit, double Scale);
    private List<Entry> _entries = [];
    private IReadOnlySet<string>? _active;

    public void SetActive(IReadOnlySet<string>? keys) => _active = keys;

    private static readonly (string Prefix, SensorKind Kind, Unit Unit, double Scale, double Min, double Max)[] Types =
    [
        ("temp",  SensorKind.Temperature, Unit.Celsius,   1e-3, 20,   100),
        ("fan",   SensorKind.Fan,         Unit.Rpm,       1.0,   0,  3000),
        ("in",    SensorKind.Voltage,     Unit.Volt,      1e-3,  0,     2),
        ("power", SensorKind.Power,       Unit.Watt,      1e-6,  0,   250),
        ("curr",  SensorKind.Other,       Unit.Amp,       1e-3,  0,    50),
        ("freq",  SensorKind.Frequency,   Unit.Megahertz, 1e-6,  0,  6000),
    ];

    public IEnumerable<SensorDescriptor> Discover()
    {
        var found = new List<SensorDescriptor>();
        var entries = new List<Entry>();

        if (!Directory.Exists(Root)) { _entries = entries; return found; }

        foreach (string dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string driver = ReadText(Path.Combine(dir, "name")) ?? Path.GetFileName(dir);
            // Resolve the SPECIFIC part, not just the driver: four NVMe drives all
            // report driver "nvme", and collapsing them into one device makes the
            // list useless. Model strings come from the parent device node.
            string device = InstanceName(dir, driver);

            foreach (var (prefix, kind, unit, scale, min, max) in Types)
            {
                IEnumerable<string> inputs;
                try { inputs = Directory.EnumerateFiles(dir, prefix + "*_input"); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (string input in inputs.OrderBy(f => f, StringComparer.Ordinal))
                {
                    string stem = Path.GetFileName(input)[..^"_input".Length];
                    string label = ReadText(Path.Combine(dir, stem + "_label")) ?? stem;

                    // Skip inputs that are present but unreadable, so the list only
                    // ever offers sensors that actually produce a number.
                    if (!TryRead(input, out _)) continue;

                    string key = $"hwmon.{Sanitise(driver)}.{Sanitise(label)}";
                    // hwmon can expose the same label twice (two identical NVMe drives).
                    string unique = key;
                    for (int n = 2; entries.Any(e => e.Key == unique); n++) unique = $"{key}#{n}";

                    entries.Add(new Entry(unique, input, unit, scale));
                    found.Add(new SensorDescriptor(unique, label, device, kind, unit, min, max,
                                                   DeviceCatalog.Resolve(driver).Category));
                }
            }
        }

        _entries = entries;
        return found;
    }

    public void Sample(IDictionary<string, double> into)
    {
        foreach (var e in _entries)
        {
            if (_active is not null && !_active.Contains(e.Key)) continue;
            if (TryRead(e.Path, out double raw))
                into[e.Key] = raw * e.Scale;
        }
    }


    /// <summary>
    /// A name for THIS hwmon instance rather than its driver. NVMe and disk nodes
    /// expose a model string; identical modules (two DIMMs) get a stable index so
    /// they remain distinguishable.
    /// </summary>
    /// <summary>
    /// Names THIS hwmon instance rather than its driver. Two rules:
    ///  - devices with a real model string (NVMe, SATA drives) use it, suffixed
    ///    with the kernel node when several identical drives are fitted, since
    ///    "SAMSUNG SSD 990 PRO 4TB" x3 collapsing into one entry is useless;
    ///  - everything else takes the friendly name from DeviceCatalog, so k10temp
    ///    becomes the CPU model and amdgpu becomes "AMD Radeon Graphics".
    /// </summary>
    private static string InstanceName(string dir, string driver)
    {
        string d = driver.ToLowerInvariant();
        string? node = LinkName(Path.Combine(dir, "device"));

        if (d is "nvme" or "drivetemp")
        {
            string? model = ReadText(Path.Combine(dir, "device", "model"));
            if (!string.IsNullOrEmpty(model))
                return node is null ? model : $"{model} ({node})";
        }

        if (d.StartsWith("spd", StringComparison.Ordinal))
            return node is null ? "Memory Module" : $"Memory Module {node}";

        return DeviceCatalog.Resolve(driver).Name;
    }

    /// <summary>Basename of the device symlink target, e.g. "nvme0" or an i2c
    /// address — stable across boots in a way hwmonN is not.</summary>
    private static string? LinkName(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null ? null : Path.GetFileName(target.FullName);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Sanitise(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s)
            buf[n++] = char.IsLetterOrDigit(c) ? c : '_';
        return new string(buf[..n]).Trim('_');
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool TryRead(string path, out double value)
    {
        value = 0;
        string? t = ReadText(path);
        return t is not null &&
               double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

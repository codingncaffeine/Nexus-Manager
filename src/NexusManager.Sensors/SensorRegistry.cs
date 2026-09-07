namespace NexusManager.Sensors;

/// <summary>
/// Everything the machine can report, discovered once and sampled on demand.
/// Sensors are grouped by device so a picker can present them the way iCUE does —
/// "AMD Ryzen 9 9950X3D / Processor" with its readings underneath — rather than
/// as a flat list of driver names.
/// </summary>
public sealed class SensorRegistry : IDisposable
{
    private readonly List<ISensorSource> _sources = [];
    private readonly Dictionary<string, double> _values = [];
    private readonly List<SensorDescriptor> _all = [];

    public TemperatureScale Scale { get; set; } = TemperatureScale.Celsius;

    public IReadOnlyList<SensorDescriptor> All => _all;

    public static SensorRegistry CreateDefault()
    {
        var r = new SensorRegistry();
        r._sources.Add(new CpuLoadSource());
        r._sources.Add(new MemorySource());
        r._sources.Add(new HwmonSource());
        r._sources.Add(new NetworkSource());
        var nv = new NvidiaSource();
        r._sources.Add(nv);
        r.Discover();
        nv.Start(TimeSpan.FromSeconds(1));
        return r;
    }

    private readonly Dictionary<string, SensorDescriptor> _index = new(StringComparer.Ordinal);

    public void Discover()
    {
        _all.Clear();
        foreach (var s in _sources) _all.AddRange(s.Discover());
        _index.Clear();
        foreach (var d in _all) _index[d.Key] = d;
    }

    /// <summary>Limits sampling to the keys given. Call with the sensors a
    /// screen actually shows.</summary>
    public void SetActive(IEnumerable<string>? keys)
    {
        var set = keys is null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var s in _sources) s.SetActive(set);
    }

    public void Sample()
    {
        foreach (var s in _sources) s.Sample(_values);
    }

    /// <summary>Reading in the user's preferred scale, or NaN if unavailable.</summary>
    public double Read(string key)
    {
        if (!_values.TryGetValue(key, out double v)) return double.NaN;
        // Descriptor lookup was a linear scan over ~70 items on every read,
        // four times a frame.
        _index.TryGetValue(key, out var d);
        return d?.Unit == Unit.Celsius ? Units.Convert(v, Scale) : v;
    }

    public SensorDescriptor? Describe(string key) =>
        _index.TryGetValue(key, out var d) ? d : null;

    /// <summary>Sensors grouped by device, ordered by category then name — the
    /// shape a picker UI needs.</summary>
    public IEnumerable<IGrouping<DeviceInfo, SensorDescriptor>> ByDevice() =>
        _all.GroupBy(s => Resolve(s), DeviceInfoComparer.Instance)
            .OrderBy(g => g.Key.Category)
            .ThenBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Each source declares its own category; the device string is already
    /// the human-facing name, so no driver-name translation is needed here.</summary>
    private static DeviceInfo Resolve(SensorDescriptor s) =>
        new(s.Device, s.Device,
            s.Category != DeviceCategory.Unknown ? s.Category : CategoryFor(s.Kind));

    private static DeviceCategory CategoryFor(SensorKind kind) => kind switch
    {
        SensorKind.Load or SensorKind.Frequency => DeviceCategory.Processor,
        SensorKind.Memory  => DeviceCategory.Dram,
        SensorKind.Network => DeviceCategory.Network,
        SensorKind.Disk    => DeviceCategory.Storage,
        _                  => DeviceCategory.Unknown,
    };

    private sealed class DeviceInfoComparer : IEqualityComparer<DeviceInfo>
    {
        public static readonly DeviceInfoComparer Instance = new();
        public bool Equals(DeviceInfo? a, DeviceInfo? b) => a?.Name == b?.Name;
        public int GetHashCode(DeviceInfo o) => o.Name.GetHashCode(StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var s in _sources) (s as IDisposable)?.Dispose();
    }
}

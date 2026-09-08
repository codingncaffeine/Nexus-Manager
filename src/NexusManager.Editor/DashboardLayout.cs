using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusManager.Editor;

/// <summary>Per-group presentation state: width, whether graphs show, and which
/// sensors have been removed from it.</summary>
public sealed class GroupState
{
    public int Columns { get; set; } = 2;
    public bool HideGraphs { get; set; }
    public bool Hidden { get; set; }
    public HashSet<string> HiddenSensors { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Order of sensors within the group, by key. Keys absent from the
    /// list sort after the listed ones, alphabetically, so a newly discovered
    /// sensor appears rather than vanishing.</summary>
    public List<string> Order { get; set; } = [];
}

/// <summary>
/// How the dashboard and the home screen are arranged, kept beside the screen
/// config.
///
/// Renames are the important part: the kernel reports "Tccd1" and
/// "hwmon.spd5118.temp1#2", which say nothing to a person. iCUE offers the same
/// facility for the same stated reason - the reported names are not clear enough
/// to indicate what you are looking at.
/// </summary>
public sealed class DashboardLayout
{
    public List<string> Order { get; set; } = [];
    public Dictionary<string, GroupState> Groups { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Sensors pinned to the Home screen, in display order. iCUE's Home
    /// is a hand-picked shortlist, not everything - the Dashboard is where
    /// everything lives.</summary>
    public List<string> Home { get; set; } = [];

    /// <summary>Sensors whose graph is hidden, on either view. Kept here rather
    /// than on the control so the choice survives a rebuild and a restart; the
    /// first version toggled a field on the tile and silently forgot it.</summary>
    /// <summary>Sensors dragged out of their group to stand on their own, which
    /// Corsair's guide describes as extracting a sensor from a group.</summary>
    public List<string> Standalone { get; set; } = [];

    public HashSet<string> NoGraph { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public string Path { get; set; } = DefaultPath;

    public static string DefaultPath
    {
        get
        {
            string home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            if (string.IsNullOrWhiteSpace(home))
                home = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(home, "nexus-manager", "dashboard.json");
        }
    }

    public GroupState For(string device)
    {
        if (!Groups.TryGetValue(device, out var st))
        {
            st = new GroupState();
            Groups[device] = st;
        }
        return st;
    }

    public string? NameFor(string sensorKey) =>
        Names.TryGetValue(sensorKey, out var n) ? n : null;

    public void Rename(string sensorKey, string? name)
    {
        if (name is null) Names.Remove(sensorKey);
        else Names[sensorKey] = name;
    }

    public bool OnHome(string sensorKey) => Home.Contains(sensorKey);

    public void SetHome(string sensorKey, bool on)
    {
        if (on) { if (!Home.Contains(sensorKey)) Home.Add(sensorKey); }
        else Home.Remove(sensorKey);
    }

    public bool GraphHidden(string sensorKey) => NoGraph.Contains(sensorKey);

    public void SetGraph(string sensorKey, bool visible)
    {
        if (visible) NoGraph.Remove(sensorKey);
        else NoGraph.Add(sensorKey);
    }

    /// <summary>Moves <paramref name="key"/> so it lands at <paramref name="index"/>
    /// in <paramref name="list"/>. Used by every drag-to-reorder in the app, so
    /// the index arithmetic that trips up a self-move lives in exactly one place.
    /// </summary>
    public static void Move(List<string> list, string key, int index)
    {
        int from = list.IndexOf(key);
        if (from >= 0) list.RemoveAt(from);
        // Removing an earlier element shifts every later target down by one.
        if (from >= 0 && from < index) index--;
        list.Insert(Math.Clamp(index, 0, list.Count), key);
    }

    /// <summary>Orders <paramref name="keys"/> by <paramref name="order"/>, with
    /// anything unlisted appended in its original sequence. A saved order must
    /// never be able to hide a sensor that appeared after it was written.</summary>
    public static IEnumerable<T> Apply<T>(IEnumerable<T> keys, List<string> order, Func<T, string> keyOf)
    {
        var items = keys.ToList();
        return items
            .OrderBy(x => order.IndexOf(keyOf(x)) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(x => items.IndexOf(x));
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DashboardLayout Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<DashboardLayout>(File.ReadAllText(DefaultPath), Json)
                       ?? new DashboardLayout();
        }
        catch (Exception) { /* a corrupt layout must not block startup */ }
        return new DashboardLayout();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { }
    }
}

/// <summary>
/// First-run defaults for the Home screen.
///
/// Corsair's guide names the two readings worth pinning before anything else -
/// CPU package temperature and GPU temperature - so an empty Home starts there
/// rather than starting blank and making the user guess what the "+" is for.
/// </summary>
public static class HomeDefaults
{
    public static void Seed(DashboardLayout layout, NexusManager.Sensors.SensorRegistry reg)
    {
        if (layout.Home.Count > 0) return;

        var wanted = new List<Func<NexusManager.Sensors.SensorDescriptor, bool>>
        {
            // CPU package temperature, however this machine spells it.
            s => s.Kind == NexusManager.Sensors.SensorKind.Temperature
                 && s.Category == NexusManager.Sensors.DeviceCategory.Processor
                 && (s.Label.Contains("Package", StringComparison.OrdinalIgnoreCase)
                     || s.Label.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                     || s.Key.EndsWith(".temp", StringComparison.Ordinal)),
            s => s.Kind == NexusManager.Sensors.SensorKind.Temperature
                 && s.Category == NexusManager.Sensors.DeviceCategory.Gpu,
            s => s.Kind == NexusManager.Sensors.SensorKind.Load
                 && s.Category == NexusManager.Sensors.DeviceCategory.Processor,
            s => s.Kind == NexusManager.Sensors.SensorKind.Load
                 && s.Category == NexusManager.Sensors.DeviceCategory.Gpu,
        };

        foreach (var match in wanted)
        {
            var hit = reg.All.FirstOrDefault(match);
            if (hit is not null && !layout.Home.Contains(hit.Key)) layout.Home.Add(hit.Key);
        }

        // A machine that matches none of the above still gets a usable Home.
        if (layout.Home.Count == 0)
            foreach (var s in reg.All.Take(4)) layout.Home.Add(s.Key);
    }
}

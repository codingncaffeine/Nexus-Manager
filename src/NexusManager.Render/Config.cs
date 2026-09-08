using System.Text.Json;
using System.Text.Json.Serialization;
using NexusManager.Sensors;

namespace NexusManager.Render;

public static class Config
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// XDG_CONFIG_HOME/bandolet/screens.json, falling back to ~/.config. Follows
    /// the spec rather than inventing a dotfile so it lands where a Linux user
    /// expects and gets picked up by existing dotfile tooling.
    /// </summary>
    public static string Path
    {
        get
        {
            string home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            if (string.IsNullOrWhiteSpace(home))
                home = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(home, "nexus-manager", "screens.json");
        }
    }

    /// <summary>
    /// Reads the screen configuration, or null when there is none to read.
    ///
    /// ⛔ A malformed file must NEVER take the application down. It used to: a
    /// truncated or hand-edited screens.json threw straight out of Main and the
    /// process dumped core, so a single interrupted autosave left the app
    /// unstartable with no message explaining why.
    ///
    /// The broken file is MOVED ASIDE rather than deleted or overwritten. The
    /// caller falls back to a discovered configuration and then autosaves over
    /// this path within seconds, so leaving the original in place would destroy
    /// whatever the user had - which for a hand-built set of screens is the only
    /// copy they have.
    /// </summary>
    public static async Task<ScreenSet?> LoadAsync(string? explicitPath)
    {
        string p = explicitPath ?? Path;
        if (!File.Exists(p)) return null;

        string text;
        try
        {
            text = await File.ReadAllTextAsync(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  cannot read {p}: {ex.Message}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScreenSet>(text, Json);
        }
        catch (JsonException ex)
        {
            string aside = $"{p}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(p, aside);
                Console.Error.WriteLine($"  {p} is not valid JSON ({ex.Message.Trim()})");
                Console.Error.WriteLine($"  kept a copy at {aside}; starting from a discovered configuration");
            }
            catch (Exception moveEx)
            {
                Console.Error.WriteLine($"  {p} is not valid JSON and could not be moved aside: {moveEx.Message}");
            }
            return null;
        }
    }
    public static async Task SaveAsync(ScreenSet set, string? explicitPath)
    {
        string p = explicitPath ?? Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, JsonSerializer.Serialize(set, Json));
    }

    /// <summary>
    /// Builds a starter set from what this machine actually reports, rather than
    /// a fixed list that would be half-empty on different hardware.
    /// </summary>
    public static ScreenSet Discover(SensorRegistry reg, bool fahrenheit)
    {
        double LoT = fahrenheit ? 68 : 20, HiT = fahrenheit ? 203 : 95;
        string TU = fahrenheit ? "°F" : "°C";

        string? Key(Func<SensorDescriptor, bool> pred) =>
            reg.All.FirstOrDefault(pred)?.Key;

        var set = new ScreenSet();

        // 1. Overview — the four most people watch.
        var overview = new ScreenSpec { Name = "Overview" };
        string? cpuTemp = Key(s => s.Key.Contains("k10temp.Tctl", StringComparison.OrdinalIgnoreCase))
                       ?? Key(s => s.Kind == SensorKind.Temperature && s.Category == DeviceCategory.Processor);
        if (cpuTemp is not null)
            overview.Modules.Add(Mod(cpuTemp, SensorKind.Temperature, "Package", TU, LoT, HiT));
        overview.Modules.Add(Mod("cpu.load", SensorKind.Load, "Load", "%", 0, 100));
        if (Key(s => s.Key == "gpu.0.temp") is { } gt)
            overview.Modules.Add(Mod(gt, SensorKind.Temperature, "GPU", TU, LoT, HiT));
        overview.Modules.Add(Mod("mem.used.percent", SensorKind.Memory, "Memory", "%", 0, 100));
        set.Screens.Add(overview);

        // 2. GPU detail, when there is a discrete GPU worth a screen.
        if (Key(s => s.Key == "gpu.0.load") is not null)
        {
            var gpu = new ScreenSpec { Name = "GPU" };
            gpu.Modules.Add(Mod("gpu.0.temp",  SensorKind.Temperature, "Temp",  TU, LoT, HiT));
            gpu.Modules.Add(Mod("gpu.0.load",  SensorKind.Load,        "Load",  "%", 0, 100));
            gpu.Modules.Add(Mod("gpu.0.power", SensorKind.Power,       "Power", "W", 0, 450));
            gpu.Modules.Add(Mod("gpu.0.mem.used", SensorKind.Memory,   "VRAM",  "GB", 0, 16));
            set.Screens.Add(gpu);
        }

        // 3. Storage — one module per drive, up to what fits.
        var drives = reg.All
            .Where(s => s.Category == DeviceCategory.Storage && s.Label.StartsWith("Composite", StringComparison.Ordinal))
            .Take(4).ToList();
        if (drives.Count > 0)
        {
            var storage = new ScreenSpec { Name = "Storage" };
            int n = 1;
            foreach (var d in drives)
                storage.Modules.Add(Mod(d.Key, SensorKind.Temperature, $"Drive {n++}", TU, LoT, HiT));
            set.Screens.Add(storage);
        }

        return set;

        static ModuleSpec Mod(string source, SensorKind kind, string label,
                              string unit, double min, double max) => new()
        {
            Source = source, Kind = kind, Label = label, Unit = unit, Min = min, Max = max,
        };
    }
}

namespace NexusManager.Sensors;

/// <summary>Physical quantity a sensor reports, before any display preference.</summary>
public enum Unit
{
    None, Celsius, Percent, Rpm, Volt, Watt, Amp, Megahertz, Bytes, BytesPerSec,
}

/// <summary>How the user wants temperatures shown. Stored separately from the
/// sensor's native unit so the reading itself is never lossy.</summary>
public enum TemperatureScale { Celsius, Fahrenheit, Kelvin }

public enum SensorKind
{
    Other, Temperature, Load, Voltage, Frequency, Fan, Power, Memory, Disk, Network,
}

/// <summary>One discoverable sensor.</summary>
public sealed record SensorDescriptor(
    string Key,
    string Label,
    string Device,
    SensorKind Kind,
    Unit Unit,
    double SuggestedMin = 0,
    double SuggestedMax = 100,
    DeviceCategory Category = DeviceCategory.Unknown);

public static class Units
{
    public static double Convert(double celsius, TemperatureScale scale) => scale switch
    {
        TemperatureScale.Fahrenheit => celsius * 9.0 / 5.0 + 32.0,
        TemperatureScale.Kelvin     => celsius + 273.15,
        _                           => celsius,
    };

    public static string Symbol(Unit unit, TemperatureScale scale) => unit switch
    {
        Unit.Celsius     => scale switch
        {
            TemperatureScale.Fahrenheit => "°F",
            TemperatureScale.Kelvin     => "K",
            _                           => "°C",
        },
        Unit.Percent     => "%",
        Unit.Rpm         => "RPM",
        Unit.Volt        => "V",
        Unit.Watt        => "W",
        Unit.Amp         => "A",
        Unit.Megahertz   => "MHz",
        Unit.Bytes       => "GB",
        Unit.BytesPerSec => "MB/s",
        _                => "",
    };

    /// <summary>Converts a Celsius range to the display scale so chart bounds
    /// track the unit rather than being silently wrong in Fahrenheit.</summary>
    public static (double Min, double Max) ConvertRange(
        double min, double max, Unit unit, TemperatureScale scale) =>
        unit == Unit.Celsius ? (Convert(min, scale), Convert(max, scale)) : (min, max);
}

public interface ISensorSource
{
    IEnumerable<SensorDescriptor> Discover();
    /// <summary>Reads current values into <paramref name="into"/>, keyed as discovered.</summary>
    void Sample(IDictionary<string, double> into);

    /// <summary>Restricts sampling to the keys actually being displayed.
    /// Reading every sensor to show four is the difference between a 41 ms
    /// frame and a 1 ms one — some hwmon reads are SMBus transactions costing
    /// milliseconds each. Sources that are already cheap may ignore this.</summary>
    void SetActive(IReadOnlySet<string>? keys) { }
}
    /// <summary>Reads current values into <paramref name="into"/>, keyed as discovered.</summary>

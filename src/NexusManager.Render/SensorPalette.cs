using NexusManager.Sensors;

namespace NexusManager.Render;

/// <summary>
/// Default colours per sensor type. iCUE colours its tiles by TYPE rather than by
/// alarm state — temperature orange, load green, voltage violet — so the palette
/// says what you are looking at before you read the number. Any module may
/// override these.
/// </summary>
public static class SensorPalette
{
    /// <summary>
    /// ⛔ Judge these ON THE PANEL, not on a monitor (D12): the green primary is
    /// yellow-shifted, so Load renders closer to lime than green.
    /// </summary>
    public static string DefaultColor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "#E8913C",   // iCUE amber-orange
        SensorKind.Load        => "#5BD75B",   // iCUE green
        SensorKind.Voltage     => "#B44FE0",   // iCUE violet
        SensorKind.Frequency   => "#4CC9F0",
        SensorKind.Fan         => "#3B9AE1",   // iCUE blue
        SensorKind.Power       => "#FFD24A",
        SensorKind.Memory      => "#F472B6",
        SensorKind.Disk        => "#94A3B8",
        SensorKind.Network     => "#38BDF8",
        _                      => "#EBEBF0",
    };

    /// <summary>Decimals iCUE shows per type: temperatures and volts carry them,
    /// load and fan speed do not.</summary>
    public static int DefaultDecimals(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 2,
        SensorKind.Voltage     => 2,
        _                      => 0,
    };
}

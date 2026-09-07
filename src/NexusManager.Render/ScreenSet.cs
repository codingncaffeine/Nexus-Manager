using System.Text.Json.Serialization;

namespace NexusManager.Render;

/// <summary>
/// The whole configuration: several screens the user swipes between.
///
/// A 640x48 strip holds about four readable modules, so a single screen cannot
/// reach the ~70 sensors a machine reports. Screens are the answer, and swipe is
/// one of only two gestures the hardware supports (Corsair's own documentation:
/// "one finger pressing or swiping left-and-right").
/// </summary>
public sealed class ScreenSet
{
    public List<ScreenSpec> Screens { get; set; } = [];

    /// <summary>Screen shown at startup, clamped to the available range.</summary>
    public int StartScreen { get; set; }

    /// <summary>Backlight 0-100.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Render rate. 24 matches Corsair's own GIF playback rate and costs
    /// 38% of the measured frame budget; the panel tops out near 65.</summary>
    public int TargetFps { get; set; } = 24;

    /// <summary>How often sensors are read, in milliseconds. Deliberately far
    /// slower than the frame rate: reading sensors per frame cost 41 ms of a
    /// 41.7 ms budget, and nothing here changes meaningfully at 24 Hz.</summary>
    public int SampleIntervalMs { get; set; } = 200;

    /// <summary>Draw page dots when more than one screen exists, so it is obvious
    /// there is more to swipe to.</summary>
    public bool ShowPageIndicator { get; set; } = true;

    /// <summary>Wrap from the last screen back to the first.</summary>
    public bool WrapScreens { get; set; } = true;

    [JsonIgnore] public bool IsEmpty => Screens.Count == 0;
}

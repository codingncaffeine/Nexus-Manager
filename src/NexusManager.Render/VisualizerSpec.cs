using System.Text.Json.Serialization;

namespace NexusManager.Render;

/// <summary>The visual modes. One shared analysis chain feeds all of them, so a
/// mode costs a draw routine rather than an analyser. Only the modes listed in
/// VisualizerRenderer.Implemented are built (D55).
public enum VisualizerKind
{
    Bars = 0,
    MirroredBars,
    GradientBars,
    SpectrumCurve,
    DualChannelSpectrum,
    Oscilloscope,
    FilledScope,
    EnvelopeMirror,
    VuMeters,
    LevelBar,
    SegmentedVu,
    Spectrogram,
    ReactiveBackground,
    BeatPulse,
    DotMatrix,

    // --- Winamp heritage. The reference the user actually wants matched:
    // the main-window analyser and scope, and the two AVS/MilkDrop ideas
    // that survive being 48 pixels tall.

    /// <summary>The main-window analyser: bars under a fixed vertical
    /// green-yellow-red ramp, with pale falling peak caps.</summary>
    WinampSpectrum,

    /// <summary>The main-window scope: a connected trace, each column
    /// coloured by its own amplitude.</summary>
    WinampScope,

    /// <summary>MilkDrop in miniature: a feedback buffer zoomed and faded
    /// every frame with the waveform drawn into it.</summary>
    Feedback,

    /// <summary>Demoscene fire, seeded from bass energy.</summary>
    Fire,
}

/// <summary>How a visualizer cell is coloured.</summary>
public enum VisualizerPalette
{
    /// <summary>Hue ramps across the band axis, bass warm to treble cool.
    /// ⛔ Provisional until seen on the panel: the green primary is
    /// yellow-shifted, so sRGB predictions do not hold (D54).</summary>
    Frequency = 0,

    /// <summary>One colour, brightness by level.</summary>
    Solid,

    /// <summary>The screen theme's value colour.</summary>
    Theme,
}

/// <summary>One visualizer cell on a screen. Shares the strip's weight budget
/// with sensor modules and buttons (D50).</summary>
public sealed class VisualizerSpec
{
    public VisualizerKind Kind { get; set; } = VisualizerKind.Bars;
    public VisualizerPalette Palette { get; set; } = VisualizerPalette.Frequency;

    /// <summary>Bands drawn. Also the analyser's band count when this is the
    /// only visualizer on screen.</summary>
    public int BandCount { get; set; } = 32;

    /// <summary>Pixels between bars. 1 keeps bars distinct at 32 bands across
    /// the full strip; 0 gives a continuous block.</summary>
    public int Gap { get; set; } = 1;

    /// <summary>Draw the peak-hold caps. At 48px these carry much of the
    /// perceived detail (D49).</summary>
    public bool ShowPeaks { get; set; } = true;

    /// <summary>Base colour for <see cref="VisualizerPalette.Solid"/>.</summary>
    public string Color { get; set; } = "#3B9AE1";

    /// <summary>Share of the strip, same units as a module's weight.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Floor under every bar so a silent display still shows the shape
    /// of the analyser rather than an empty strip.</summary>
    public bool ShowBaseline { get; set; } = true;

    [JsonIgnore] public int EffectiveBands => Math.Clamp(BandCount, 4, 128);
}

using System.Text.Json.Serialization;

namespace NexusManager.Render;

/// <summary>
/// The visual modes. One shared analysis chain feeds all of them, so a mode
/// costs a draw routine rather than an analyser. Only the modes listed in
/// <see cref="VisualizerRenderer.Implemented"/> are built (D55).
///
/// ⛔ Values are explicit and MUST NOT be renumbered: the chosen mode is
/// persisted by name in settings.json, but a reordered enum silently changes
/// what an existing config means.
/// </summary>
public enum VisualizerKind
{
    // --- Spectrum ---
    Bars = 0,
    MirroredBars = 1,
    GradientBars = 2,
    SpectrumCurve = 3,
    DualChannelSpectrum = 4,
    SegmentedVu = 5,
    DotMatrix = 6,

    // --- Waveform ---
    Oscilloscope = 7,
    FilledScope = 8,
    EnvelopeMirror = 9,

    // --- Level ---
    VuMeters = 10,
    LevelBar = 11,

    // --- Time/frequency ---
    Spectrogram = 12,

    // --- Effects ---
    ReactiveBackground = 13,
    BeatPulse = 14,

    // --- Winamp heritage ---
    /// <summary>The main-window analyser: bars under a fixed vertical
    /// green-yellow-red ramp anchored to the CELL, with pale falling caps.</summary>
    WinampSpectrum = 15,

    /// <summary>The main-window scope: a connected trace, each column coloured
    /// by its own amplitude.</summary>
    WinampScope = 16,

    /// <summary>MilkDrop in miniature: a feedback buffer zoomed and faded every
    /// frame with the waveform drawn into it.</summary>
    Feedback = 17,

    /// <summary>Demoscene fire, seeded from the spectrum.</summary>
    Fire = 18,

    /// <summary>AVS's superscope: a parametric curve driven by the waveform.</summary>
    Superscope = 19,

    /// <summary>Beat-reactive perspective dots.</summary>
    Starfield = 20,

    /// <summary>Sine-field plasma, hue-cycled on the beat.</summary>
    Plasma = 21,

    // --- Windows Media Player heritage ---
    /// <summary>WMP's "Bars": SOLID bars under DETACHED white caps. Distinct
    /// from <see cref="WinampSpectrum"/>, which ramps colour with height -
    /// checked against a WMP 11 screenshot, whose bars are one flat green.</summary>
    WmpBars = 22,

    /// <summary>WMP's unused "Dot Scope" preset: the oscilloscope drawn as
    /// unconnected dots rather than a trace.</summary>
    DotScope = 23,

    /// <summary>WMP's "Particle" lineage: a fountain thrown by band energy.</summary>
    Particles = 24,

    /// <summary>WMP's "Ambience" lineage: a slow flowing colour field.</summary>
    Ambience = 25,

    /// <summary>The "Battery" lineage: the spectrum folded and mirrored into a
    /// symmetric figure.</summary>
    Kaleidoscope = 26,

    /// <summary>Stereo goniometer: left plotted against right. The one mode that
    /// cannot be derived from the mono mix, because the mix is exactly the
    /// information it exists to show.</summary>
    Vectorscope = 27,

    // --- Modern ---

    /// <summary>Bars over a mirrored, fading reflection under a bright
    /// horizon line - the look most modern music-video visualizers use.</summary>
    ReflectedBars = 28,

    /// <summary>Rounded capsule bars with a bloom halo - the clean, glowing
    /// look most current web and video visualizers use.</summary>
    GlowPills = 29,

    /// <summary>Metaballs: per-band blobs that merge into each other as they
    /// grow. The lava-lamp look, and the only mode here with no hard edge
    /// anywhere in it.</summary>
    Blobs = 30,

    /// <summary>Waveform terrain: a stack of past spectra drawn as receding
    /// ridgelines, so the display carries several seconds of history as
    /// depth rather than as scroll.</summary>
    Terrain = 31,
}

/// <summary>
/// How a visualizer cell is coloured.
///
/// The named schemes come from Windows Media Player, where "Bars", "Ocean Mist"
/// and "Fire Storm" are the SAME analyser under different colours. Palettes
/// rather than duplicate modes, for the same reason.
/// </summary>
public enum VisualizerPalette
{
    /// <summary>Hue ramps across the band axis, bass warm to treble cool.
    /// ⛔ Provisional until seen on the panel: the green primary is
    /// yellow-shifted, so sRGB predictions do not hold (D54).</summary>
    Frequency = 0,

    /// <summary>One colour, from <see cref="VisualizerSpec.Color"/>.</summary>
    Solid = 1,

    /// <summary>The screen theme's value colour.</summary>
    Theme = 2,

    /// <summary>Cool blues and cyan, after WMP's "Ocean Mist".</summary>
    OceanMist = 3,

    /// <summary>Reds through orange to yellow, after WMP's "Fire Storm".</summary>
    FireStorm = 4,

    /// <summary>Green through yellow to red by HEIGHT, the meter convention.</summary>
    Meter = 5,

    /// <summary>Flat green, as WMP's own "Bars" draws it.</summary>
    Emerald = 6,
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

    /// <summary>
    /// Peak cap colour. WHITE by default and independent of the bar colour,
    /// because that is what the meters this imitates actually did: WMP's own
    /// analyser is flat green bars under clearly detached white caps. Tinting
    /// the cap to match the bar makes it read as part of the bar instead.
    /// </summary>
    public string PeakColor { get; set; } = "#E8E8F0";

    /// <summary>Base colour for <see cref="VisualizerPalette.Solid"/>.</summary>
    public string Color { get; set; } = "#3B9AE1";

    /// <summary>Share of the strip, same units as a module's weight.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Floor under every bar so a silent display still shows the shape
    /// of the analyser rather than an empty strip.</summary>
    public bool ShowBaseline { get; set; } = true;

    /// <summary>Only <see cref="VisualizerKind.ReactiveBackground"/> uses this:
    /// the still or animation it modulates with the level.</summary>
    public BackgroundSpec Background { get; set; } = new();

    [JsonIgnore] public int EffectiveBands => Math.Clamp(BandCount, 4, 128);
}

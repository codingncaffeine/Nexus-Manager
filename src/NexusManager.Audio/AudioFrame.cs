namespace NexusManager.Audio;

/// <summary>
/// One immutable analysis snapshot, published by the capture/analysis thread and
/// read by the render loop.
///
/// Immutable and swapped by reference on purpose: the render loop must never
/// take a lock on audio, and must never see a half-written set of bands. A
/// frozen visualizer when audio stalls is acceptable; a stalled panel is not.
///
/// All band and level values are NORMALISED 0..1, where 0 is the dB floor and 1
/// is 0 dBFS. Renderers therefore never handle decibels, and a mode cannot
/// accidentally invent its own scaling.
/// </summary>
public sealed class AudioFrame
{
    /// <summary>Per-band level after tilt, envelope and normalisation.</summary>
    public required float[] Bands { get; init; }

    /// <summary>Per-band peak-hold marker, same scale as <see cref="Bands"/>.</summary>
    public required float[] Peaks { get; init; }

    /// <summary>Centre frequency of each band, Hz. Lets a renderer colour by
    /// frequency without re-deriving the band layout.</summary>
    public required float[] BandCentres { get; init; }

    /// <summary>Recent mono samples, -1..1, oldest first. For scope modes.</summary>
    public required float[] Waveform { get; init; }

    /// <summary>The same window, per channel. A vectorscope plots L against
    /// R and cannot be derived from the mono mix - the mix is exactly the
    /// information a goniometer exists to show.</summary>
    public required float[] WaveformLeft { get; init; }
    public required float[] WaveformRight { get; init; }

    /// <summary>Normalised RMS and peak per channel, for the level family.</summary>
    public float RmsLeft { get; init; }
    public float RmsRight { get; init; }
    public float PeakLeft { get; init; }
    public float PeakRight { get; init; }

    /// <summary>Set on the hop an onset was detected. Drives the beat-reactive
    /// modes. See SpectrumAnalyser: this is spectral flux, so it fires on
    /// broadband energy INCREASES rather than on loudness.</summary>
    public bool Beat { get; init; }

    /// <summary>0..1, jumps to 1 on a beat and decays. Lets a mode pulse
    /// smoothly instead of flickering for exactly one frame.</summary>
    public float BeatIntensity { get; init; }

    /// <summary>True while the signal is under the silence gate (D51).</summary>
    public bool Silent { get; init; }

    /// <summary>Unfiltered mono peak in dBFS. Diagnostics only — the probe
    /// prints this, renderers use the normalised values.</summary>
    public float PeakDbfs { get; init; }

    /// <summary>Monotonic hop counter. A render loop can tell a stalled
    /// analyser from a genuinely static signal, which look identical.</summary>
    public long Sequence { get; init; }

    /// <summary>When this hop was published, on the capture clock.</summary>
    public TimeSpan Timestamp { get; init; }

    public static AudioFrame Empty(int bands, int waveform) => new()
    {
        Bands = new float[bands],
        Peaks = new float[bands],
        BandCentres = new float[bands],
        Waveform = new float[waveform],
        WaveformLeft = new float[waveform],
        WaveformRight = new float[waveform],
        Silent = true,
        PeakDbfs = -120f,
    };
}

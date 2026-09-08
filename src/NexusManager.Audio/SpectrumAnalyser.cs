namespace NexusManager.Audio;

/// <summary>Tunables for the shared analysis chain. See D45-D49.</summary>
public sealed class AnalyserOptions
{
    public int SampleRate { get; set; } = 48000;
    public int FftSize { get; set; } = 4096;

    /// <summary>Samples between hops. 1024 at 48 kHz is 21.3 ms = 47 hops/s,
    /// comfortably ahead of a 30 fps display (D45).</summary>
    public int HopSize { get; set; } = 1024;

    public int BandCount { get; set; } = 32;

    /// <summary>Band range. 30 Hz is below the lowest note most systems
    /// reproduce; 16 kHz is above where music carries usable energy (D46).</summary>
    public float MinHz { get; set; } = 30f;
    public float MaxHz { get; set; } = 16000f;

    /// <summary>dB floor. 60 dB across 48 pixels is 1.25 dB per pixel (D49).</summary>
    public float FloorDb { get; set; } = -60f;

    /// <summary>Cosmetic spectral tilt, dB per octave above <see cref="TiltPivotHz"/>.
    /// Music slopes down with frequency, so without this the top third of the
    /// display never moves (D47). NOT a calibration.</summary>
    public float TiltDbPerOctave { get; set; }
    public float TiltPivotHz { get; set; } = 1000f;

    /// <summary>Bar fall rate. Attack is instant; symmetric smoothing misses
    /// transients (D48).</summary>
    public float DecayDbPerSecond { get; set; } = 15f;

    /// <summary>How long a cap hangs at a new high before it lets go. This
    /// pause is what makes the cap read as a separate object rather than as
    /// the top edge of the bar (D58).</summary>
    public float PeakHoldSeconds { get; set; } = 0.4f;

    /// <summary>Cap acceleration, in fractions of full scale per second
    /// squared. At 3.0 a cap released from the top reaches the floor in
    /// about 0.8 s, starting slowly and visibly gathering speed.</summary>
    public float PeakGravity { get; set; } = 3.0f;

    /// <summary>Below this the display idles instead of twitching on the noise
    /// floor of a monitor source (D51).</summary>
    public float SilenceGateDbfs { get; set; } = -55f;

    /// <summary>Samples of mono history kept for scope modes — one per panel
    /// column.</summary>
    public int WaveformLength { get; set; } = 640;
}

/// <summary>
/// The one analysis chain every visualizer mode shares: window, FFT, log-spaced
/// band aggregation, cosmetic tilt, dB conversion, envelope and peak hold.
///
/// One chain for all modes is the point. A mode costs a draw routine, not an
/// analyser, and two modes can never disagree about what the audio was doing.
/// </summary>
public sealed class SpectrumAnalyser
{
    private readonly AnalyserOptions _o;
    private readonly Fft _fft;
    private readonly float[] _window;
    private readonly float[] _magnitude;
    private readonly float[] _frameBuf;

    /// <summary>Inclusive bin range per band, and the fractional bin centre used
    /// when a band is narrower than one bin.</summary>
    private readonly int[] _binLo;
    private readonly int[] _binHi;
    private readonly float[] _binCentre;
    private readonly float[] _centres;
    private readonly float[] _tiltDb;

    private readonly float[] _bandDb;
    private readonly float[] _peakHold;

    /// <summary>Cap position and velocity in NORMALISED display units, not
    /// decibels. See the note in Process: gravity is only convincing in the
    /// space the cap is actually drawn in.</summary>
    private readonly float[] _peakNorm;
    private readonly float[] _peakVel;

    private long _sequence;

    public int BandCount => _o.BandCount;
    public IReadOnlyList<float> BandCentres => _centres;

    public SpectrumAnalyser(AnalyserOptions options)
    {
        _o = options;
        _fft = new Fft(_o.FftSize);
        _magnitude = new float[_o.FftSize / 2 + 1];
        _frameBuf = new float[_o.FftSize];

        // Hann. Without a window, spectral leakage smears every tone across its
        // neighbours and the display turns to mush (D45).
        _window = new float[_o.FftSize];
        for (int i = 0; i < _o.FftSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (_o.FftSize - 1)));

        int n = _o.BandCount;
        _binLo = new int[n];
        _binHi = new int[n];
        _binCentre = new float[n];
        _centres = new float[n];
        _tiltDb = new float[n];
        _bandDb = new float[n];
        _peakHold = new float[n];
        _peakNorm = new float[n];
        _peakVel = new float[n];

        float binHz = (float)_o.SampleRate / _o.FftSize;
        int maxBin = _o.FftSize / 2;
        double ratio = Math.Log((double)_o.MaxHz / _o.MinHz);

        for (int b = 0; b < n; b++)
        {
            // Geometric edges: equal ratio per band, so an octave occupies the
            // same width wherever it sits (D46).
            float lo = (float)(_o.MinHz * Math.Exp(ratio * b / n));
            float hi = (float)(_o.MinHz * Math.Exp(ratio * (b + 1) / n));
            _centres[b] = MathF.Sqrt(lo * hi);

            _binLo[b] = Math.Clamp((int)MathF.Floor(lo / binHz), 1, maxBin);
            _binHi[b] = Math.Clamp((int)MathF.Ceiling(hi / binHz) - 1, 1, maxBin);
            _binCentre[b] = Math.Clamp(_centres[b] / binHz, 1f, maxBin - 1f);

            _tiltDb[b] = _o.TiltDbPerOctave * MathF.Log2(_centres[b] / _o.TiltPivotHz);
            _bandDb[b] = _o.FloorDb;
        }

        _lastDb = _o.FloorDb;
    }

    private float _lastDb;

    /// <summary>
    /// Analyses one hop. <paramref name="mono"/> must be exactly FftSize samples
    /// — the most recent window, oldest first.
    /// </summary>
    public void Process(ReadOnlySpan<float> mono, float dt)
    {
        for (int i = 0; i < _o.FftSize; i++)
            _frameBuf[i] = mono[i] * _window[i];

        _fft.MagnitudeSpectrum(_frameBuf, _magnitude);

        // Hann halves coherent gain; undo it so a full-scale sine still reads
        // 0 dBFS in its own band rather than -6.
        const float HannGain = 2f;

        for (int b = 0; b < _o.BandCount; b++)
        {
            float amp;
            if (_binHi[b] >= _binLo[b])
            {
                // SUM of power across the band, which is what an RTA
                // does and what makes this read correctly on music.
                //
                // A geometric bands width grows in proportion to its
                // centre frequency, so summed band power is PSD x f.
                // Music is roughly pink (PSD proportional to 1/f), so
                // summed bands render music close to FLAT across the
                // strip with no correction at all. Mean power - power
                // DENSITY - would instead make white noise flat and
                // slope music down to the right, needing a large
                // cosmetic tilt to undo. Summing also means a pure tone
                // reads at its true level in any band, however wide,
                // which is what makes E1 able to assert a level at all.
                float power = 0f;
                for (int k = _binLo[b]; k <= _binHi[b]; k++)
                    power += _magnitude[k] * _magnitude[k];
                amp = MathF.Sqrt(power);
            }
            else
            {
                // Band narrower than one bin: interpolate rather than repeat the
                // same bin across several bands, which would show as visible
                // stair-steps at the bass end.
                int k = (int)_binCentre[b];
                float f = _binCentre[b] - k;
                amp = _magnitude[k] * (1f - f) + _magnitude[k + 1] * f;
            }

            float db = ToDb(amp * HannGain) + _tiltDb[b];
            if (db < _o.FloorDb) db = _o.FloorDb;
            if (db > 0f) db = 0f;

            // Instant attack, timed decay (D48).
            float decayed = _bandDb[b] - _o.DecayDbPerSecond * dt;
            _bandDb[b] = db > decayed ? db : decayed;
            if (_bandDb[b] < _o.FloorDb) _bandDb[b] = _o.FloorDb;

            // The falling cap, and the reason it is worth this much code: a cap
            // that merely decays looks like a second bar. A cap that HOLDS, lets
            // go, accelerates, and lands on the bar reads as a physical object
            // sitting on top of the level - which is the effect every hi-fi
            // meter and every Winamp skin was after.
            //
            // ⛔ Gravity is applied in NORMALISED DISPLAY UNITS, not decibels.
            // A constant fall in dB is not a constant fall in pixels, and an
            // ACCELERATING fall in dB is a different curve again on screen. The
            // cap has to accelerate in the space it is drawn in or it does not
            // read as falling at all.
            float norm = Normalise(_bandDb[b]);
            if (norm >= _peakNorm[b])
            {
                // Bar has caught or passed the cap: the cap rides on top, at
                // rest. This is also the collision case - a cap falling onto a
                // rising bar stops dead on it rather than sinking through.
                _peakNorm[b] = norm;
                _peakVel[b] = 0f;
                _peakHold[b] = _o.PeakHoldSeconds;
            }
            else if (_peakHold[b] > 0f)
            {
                // Detached and hanging. The bar has dropped away beneath it.
                _peakHold[b] -= dt;
            }
            else
            {
                _peakVel[b] += _o.PeakGravity * dt;
                _peakNorm[b] -= _peakVel[b] * dt;
                if (_peakNorm[b] <= norm)
                {
                    _peakNorm[b] = norm;
                    _peakVel[b] = 0f;
                }
            }
        }
    }

    /// <summary>Publishes the current state. Allocates the small arrays fresh so
    /// the frame handed to the renderer can never be mutated underneath it.</summary>
    public AudioFrame Publish(
        ReadOnlySpan<float> waveform,
        float rmsL, float rmsR, float peakL, float peakR,
        float monoPeakDbfs,
        TimeSpan timestamp)
    {
        var bands = new float[_o.BandCount];
        var peaks = new float[_o.BandCount];
        for (int b = 0; b < _o.BandCount; b++)
        {
            bands[b] = Normalise(_bandDb[b]);
            peaks[b] = Math.Clamp(_peakNorm[b], 0f, 1f);
        }

        var wave = new float[waveform.Length];
        waveform.CopyTo(wave);

        _lastDb = monoPeakDbfs;

        return new AudioFrame
        {
            Bands = bands,
            Peaks = peaks,
            BandCentres = (float[])_centres.Clone(),
            Waveform = wave,
            RmsLeft = Normalise(ToDb(rmsL)),
            RmsRight = Normalise(ToDb(rmsR)),
            PeakLeft = Normalise(ToDb(peakL)),
            PeakRight = Normalise(ToDb(peakR)),
            Silent = monoPeakDbfs < _o.SilenceGateDbfs,
            PeakDbfs = monoPeakDbfs,
            Sequence = ++_sequence,
            Timestamp = timestamp,
        };
    }

    /// <summary>Last mono peak seen, dBFS. Diagnostics.</summary>
    public float LastPeakDbfs => _lastDb;

    private float Normalise(float db) =>
        Math.Clamp((db - _o.FloorDb) / -_o.FloorDb, 0f, 1f);

    public static float ToDb(float amplitude) =>
        amplitude > 1e-7f ? 20f * MathF.Log10(amplitude) : -140f;
}

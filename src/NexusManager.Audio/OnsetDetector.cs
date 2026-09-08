namespace NexusManager.Audio;

/// <summary>
/// Beat detection by SPECTRAL FLUX: the sum of per-band energy INCREASES since
/// the last hop, compared against a rolling median of recent flux.
///
/// Flux rather than loudness, because loudness alone cannot tell a beat from a
/// sustained loud passage - a held power chord is loud on every hop and would
/// fire continuously, while a kick under a quiet passage would never fire at
/// all. Only rises count (negative differences are discarded), which is what
/// makes an onset an onset.
///
/// The threshold is a MEDIAN of recent history, not a mean: one crash cymbal
/// drags a mean up far enough to swallow the next several beats, where a median
/// barely moves.
/// </summary>
public sealed class OnsetDetector
{
    private readonly float[] _previous;
    private readonly float[] _history;
    private readonly float[] _sorted;
    private int _pos;
    private int _filled;
    private float _sinceBeat;

    /// <summary>How far above the median flux must sit to count. Below ~1.3 a
    /// steady groove fires on every hop; above ~2.0 only the loudest hits
    /// register.</summary>
    public float Sensitivity { get; set; } = 1.5f;

    /// <summary>Minimum gap between beats. 240 bpm is faster than any dance
    /// track's pulse, so anything closer is the same onset detected twice.</summary>
    public float MinIntervalSeconds { get; set; } = 0.25f;

    /// <summary>0..1, set to 1 on a beat and decaying. A mode that pulses on
    /// <see cref="Beat"/> alone flashes for a single frame and reads as a
    /// glitch rather than as a pulse.</summary>
    public float Intensity { get; private set; }

    public bool Beat { get; private set; }

    public OnsetDetector(int bandCount, int historyHops = 43)
    {
        _previous = new float[bandCount];
        _history = new float[historyHops];
        _sorted = new float[historyHops];
    }

    /// <summary>Feeds one hop of NORMALISED band levels.</summary>
    public void Process(ReadOnlySpan<float> bands, float dt)
    {
        float flux = 0f;
        for (int b = 0; b < _previous.Length && b < bands.Length; b++)
        {
            float rise = bands[b] - _previous[b];
            if (rise > 0f) flux += rise;
            _previous[b] = bands[b];
        }

        _sinceBeat += dt;
        Intensity = MathF.Max(0f, Intensity - dt * 3f);
        Beat = false;

        if (_filled >= _history.Length)
        {
            _history.CopyTo(_sorted, 0);
            Array.Sort(_sorted);
            float median = _sorted[_sorted.Length / 2];

            // The absolute floor stops a silent passage, where the median is
            // near zero, from making every speck of noise a beat.
            if (flux > median * Sensitivity && flux > 0.05f && _sinceBeat >= MinIntervalSeconds)
            {
                Beat = true;
                Intensity = 1f;
                _sinceBeat = 0f;
            }
        }

        _history[_pos] = flux;
        _pos = (_pos + 1) % _history.Length;
        if (_filled < _history.Length) _filled++;
    }
}

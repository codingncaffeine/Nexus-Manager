namespace NexusManager.Audio;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT, real input.
///
/// Deliberately the straightforward complex transform with the imaginary part
/// zeroed, rather than the real-input packing trick that halves the work. At
/// N=4096 this is roughly 49k butterflies per hop and hops arrive at 47 Hz, so
/// the whole analyser is far inside a 33 ms frame budget either way. The packing
/// trick buys 2x on something that is not the bottleneck, at the cost of an
/// unpacking step that is easy to get subtly wrong.
///
/// ⛔ If this ever DOES show up in a profile, optimise it then, with the
/// measurement in hand. Do not pre-optimise it on the strength of this comment.
/// </summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int _levels;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly int[] _reverse;

    /// <summary>Scratch, reused every call so a hop allocates nothing.</summary>
    private readonly float[] _re;
    private readonly float[] _im;

    public int Size => _n;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two, got {n}.", nameof(n));

        _n = n;
        _levels = System.Numerics.BitOperations.TrailingZeroCount((uint)n);
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            double angle = -2.0 * Math.PI * i / n;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }

        // Bit-reversal permutation, precomputed: the inner loop is hot and the
        // table is a few KB.
        _reverse = new int[n];
        for (int i = 0; i < n; i++)
            _reverse[i] = (int)(ReverseBits((uint)i) >> (32 - _levels));

        _re = new float[n];
        _im = new float[n];
    }

    private static uint ReverseBits(uint x)
    {
        x = ((x & 0x55555555u) << 1) | ((x >> 1) & 0x55555555u);
        x = ((x & 0x33333333u) << 2) | ((x >> 2) & 0x33333333u);
        x = ((x & 0x0F0F0F0Fu) << 4) | ((x >> 4) & 0x0F0F0F0Fu);
        x = ((x & 0x00FF00FFu) << 8) | ((x >> 8) & 0x00FF00FFu);
        return (x << 16) | (x >> 16);
    }

    /// <summary>
    /// Transforms <paramref name="input"/> (length N) and writes the magnitude of
    /// bins 0..N/2 into <paramref name="magnitude"/> (length N/2 + 1).
    ///
    /// Magnitudes are normalised by N/2 so a full-scale sine reads 1.0 in its
    /// own bin, which is what makes the dBFS conversion downstream meaningful
    /// rather than merely proportional.
    /// </summary>
    public void MagnitudeSpectrum(ReadOnlySpan<float> input, Span<float> magnitude)
    {
        if (input.Length != _n)
            throw new ArgumentException($"Input must be {_n} samples, got {input.Length}.", nameof(input));
        if (magnitude.Length != _n / 2 + 1)
            throw new ArgumentException($"Magnitude must be {_n / 2 + 1} bins, got {magnitude.Length}.", nameof(magnitude));

        for (int i = 0; i < _n; i++)
        {
            _re[_reverse[i]] = input[i];
            _im[i] = 0f;
        }

        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1;
            int step = _n / size;
            for (int i = 0; i < _n; i += size)
            {
                for (int j = i, k = 0; j < i + half; j++, k += step)
                {
                    int l = j + half;
                    float tre =  _re[l] * _cos[k] - _im[l] * _sin[k];
                    float tim =  _re[l] * _sin[k] + _im[l] * _cos[k];
                    _re[l] = _re[j] - tre;
                    _im[l] = _im[j] - tim;
                    _re[j] += tre;
                    _im[j] += tim;
                }
            }
        }

        // DC and Nyquist are not doubled; every other bin carries half its
        // energy in the mirrored negative frequency.
        float scale = 2f / _n;
        magnitude[0] = MathF.Abs(_re[0]) / _n;
        for (int i = 1; i < _n / 2; i++)
            magnitude[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * scale;
        magnitude[_n / 2] = MathF.Abs(_re[_n / 2]) / _n;
    }
}

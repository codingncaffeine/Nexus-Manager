namespace NexusManager.Audio;

/// <summary>
/// Fixed-capacity circular sample buffer with ordered copy-out.
///
/// ⛔ This type exists because the obvious alternative is catastrophically slow
/// and does not look it. Shifting a 4096-sample analysis window left by one per
/// arriving sample is an Array.Copy per sample: at 48 kHz that is 786 MB/s of
/// memmove for the window alone, to feed an analyser that consumes it 47 times a
/// second. Writing to a ring and copying out ONCE PER HOP is 192k floats/s, four
/// thousand times less work for identical output.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buf;
    private int _pos;
    private long _written;

    public int Capacity => _buf.Length;

    /// <summary>Total samples ever written. Used to tell "buffer not full yet"
    /// from "buffer full of silence", which are not the same thing.</summary>
    public long Written => _written;

    public bool Full => _written >= _buf.Length;

    public RingBuffer(int capacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buf = new float[capacity];
    }

    public void Add(float value)
    {
        _buf[_pos] = value;
        if (++_pos == _buf.Length) _pos = 0;
        _written++;
    }

    /// <summary>Copies the contents into <paramref name="destination"/> oldest
    /// first. Destination must match capacity exactly.</summary>
    public void CopyOrdered(Span<float> destination)
    {
        if (destination.Length != _buf.Length)
            throw new ArgumentException(
                $"Destination must be {_buf.Length} samples, got {destination.Length}.",
                nameof(destination));

        // Two memmoves rather than a per-element loop: from the write cursor to
        // the end, then the wrapped head.
        int tail = _buf.Length - _pos;
        _buf.AsSpan(_pos, tail).CopyTo(destination);
        _buf.AsSpan(0, _pos).CopyTo(destination[tail..]);
    }
}

namespace NexusManager.Render;

/// <summary>
/// Fixed-capacity ring of recent samples for a strip chart. Capacity is normally
/// the pixel width of the chart, so one sample maps to one column and nothing has
/// to be resampled at draw time.
/// </summary>
public sealed class History
{
    private readonly double[] _samples;
    private int _next;

    /// <summary>Lowest value seen since tracking began. iCUE shows this beside the
    /// reading, and it is the part a glance at the current number cannot give you:
    /// whether a spike happened while you were not looking.</summary>
    public double Min { get; private set; } = double.PositiveInfinity;
    public double Max { get; private set; } = double.NegativeInfinity;
    public bool HasRange => Count > 0 && !double.IsInfinity(Min);

    /// <summary>
    /// Lowest and highest across the samples actually HELD — the visible window,
    /// not the lifetime extremes above.
    ///
    /// ⛔ These are different things and the distinction is the whole point.
    /// Min/Max are since tracking began, which is what belongs beside the
    /// reading. A chart auto-ranging to those would go flat again the moment one
    /// spike widened them permanently; it has to follow what is on screen.
    /// </summary>
    public (double Lo, double Hi) Window()
    {
        if (Count == 0) return (double.NaN, double.NaN);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < Count; i++)
        {
            double v = _samples[i];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        return (lo, hi);
    }

    public int Capacity => _samples.Length;
    public int Count { get; private set; }

    public History(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _samples = new double[capacity];
    }

    public void Add(double value)
    {
        _samples[_next] = value;
        _next = (_next + 1) % _samples.Length;
        if (Count < _samples.Length) Count++;
        if (value < Min) Min = value;
        if (value > Max) Max = value;
    }

    /// <summary>
    /// Sample at <paramref name="age"/> positions back from newest (0 = newest).
    /// </summary>
    public double this[int age]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(age);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(age, Count);
            int i = _next - 1 - age;
            if (i < 0) i += _samples.Length;
            return _samples[i];
        }
    }
}

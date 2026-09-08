namespace NexusManager.Actions;

/// <summary>One event in a macro.</summary>
public enum MacroStepKind
{
    /// <summary>Press a key and hold it.</summary>
    KeyDown,
    /// <summary>Release a key.</summary>
    KeyUp,
    /// <summary>Press and release, the common case.</summary>
    Tap,
    /// <summary>Wait.</summary>
    Delay,
}

/// <summary>
/// A single step. Press and release are separate kinds rather than one "key"
/// step, because that is what makes a macro able to hold a modifier across
/// several other keys - which is most of what people write macros for.
/// </summary>
public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; } = MacroStepKind.Tap;

    /// <summary>Key name for KeyDown, KeyUp and Tap. Same vocabulary as the
    /// single-key action.</summary>
    public string Key { get; set; } = "";

    /// <summary>Milliseconds, for Delay.</summary>
    public int DelayMs { get; set; } = 50;

    public override string ToString() => Kind switch
    {
        MacroStepKind.Delay => $"wait {DelayMs} ms",
        MacroStepKind.KeyDown => $"hold {Key}",
        MacroStepKind.KeyUp => $"release {Key}",
        _ => $"tap {Key}",
    };
}

/// <summary>How many times a macro runs.</summary>
public enum MacroRepeat
{
    /// <summary>Run the steps once per press.</summary>
    Once,
    /// <summary>Run them <see cref="MacroSpec.RepeatCount"/> times.</summary>
    Count,
    /// <summary>Keep running until the button is pressed again. There is no
    /// "while held": the panel reports taps, not a held contact, so a
    /// hold-to-repeat macro cannot be expressed on this hardware.</summary>
    UntilPressedAgain,
}

/// <summary>
/// An ordered list of key events and waits.
///
/// The shape is deliberately Corsair's own — an event list rather than a
/// recorded blob — so that a `.cuescreens` macro maps onto it one-for-one, and
/// so recording can be added later as an input METHOD without changing anything
/// that is stored (D28).
/// </summary>
public sealed class MacroSpec
{
    public List<MacroStep> Steps { get; set; } = [];

    public MacroRepeat Repeat { get; set; } = MacroRepeat.Once;

    /// <summary>Used only by <see cref="MacroRepeat.Count"/>. Clamped on use;
    /// a config asking for a million repeats is a mistake, not an intention.
    /// </summary>
    public int RepeatCount { get; set; } = 2;

    /// <summary>Milliseconds between one pass and the next when repeating.</summary>
    public int RepeatDelayMs { get; set; } = 100;

    public override string ToString()
    {
        string what = Steps.Count == 1 ? "1 step" : $"{Steps.Count} steps";
        return Repeat switch
        {
            MacroRepeat.Count => $"{what}, x{RepeatCount}",
            MacroRepeat.UntilPressedAgain => $"{what}, until pressed again",
            _ => what,
        };
    }
}

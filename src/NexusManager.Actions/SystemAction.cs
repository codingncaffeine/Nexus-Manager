namespace NexusManager.Actions;

/// <summary>What a touch button does when it is pressed.</summary>
public enum ActionKind
{
    None,
    /// <summary>Jump to another screen. This is .cuescreens' nestedScreenId, and
    /// the reason screens form a tree rather than a flat carousel (D10).</summary>
    Screen,
    /// <summary>Run a program or shell command.</summary>
    Launch,
    /// <summary>Default sink volume: up, down or mute.</summary>
    Volume,
    /// <summary>MPRIS transport: play-pause, next, previous, stop.</summary>
    Media,
    /// <summary>Panel backlight.</summary>
    Brightness,
    /// <summary>Synthetic key press through a uinput virtual keyboard. This is
    /// iCUE's macro button. Wayland has no XTEST, so there is no other way.</summary>
    Key,

    /// <summary>An ordered list of key events and waits - Corsair's macro.
    /// <see cref="ActionKind.Key"/> is the one-key case of this and is kept
    /// because it is what most buttons want and needs no editor.</summary>
    Macro,
}

/// <summary>
/// One thing a button does. Deliberately a small closed vocabulary rather than
/// "run this shell string for everything": volume and media want to work the
/// same way on any desktop, and hiding them behind a command line would push
/// that problem onto the user.
/// </summary>
public sealed class SystemAction
{
    public ActionKind Kind { get; set; } = ActionKind.None;

    /// <summary>
    /// What the action acts on, per kind:
    ///   Screen     — the screen's name
    ///   Launch     — a command line, run through the shell
    ///   Volume     — "up" | "down" | "mute"
    ///   Media      — "play-pause" | "next" | "previous" | "stop"
    ///   Brightness — "up" | "down"
    ///   Key        — a key name, or several joined by "+" for a chord
    /// </summary>
    public string? Target { get; set; }

    /// <summary>The macro, when Kind is Macro. Null for every other kind.</summary>
    public MacroSpec? Macro { get; set; }

    /// <summary>Step size for volume and brightness, in percent.</summary>
    public double Amount { get; set; } = 5;

    public override string ToString() => Kind switch
    {
        ActionKind.None => "nothing",
        ActionKind.Screen => $"go to screen '{Target}'",
        ActionKind.Launch => $"run {Target}",
        ActionKind.Volume => $"volume {Target} {Amount:0}%",
        ActionKind.Media => $"media {Target}",
        ActionKind.Brightness => $"brightness {Target} {Amount:0}%",
        ActionKind.Key => $"press {Target}",
        ActionKind.Macro => Macro is null ? "macro (empty)" : $"macro: {Macro}",
        _ => Kind.ToString(),
    };
}
